using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BabelRead.Core.Translation;

/// <summary>
/// Translates a page one segment (paragraph) at a time, reusing anything the store already holds and
/// persisting each segment the moment it is produced. Translating in segments is what makes the work
/// durable: segments are keyed by their own text, so re-cutting the book into different pages never
/// throws any of it away, and a page abandoned half-way still banks the paragraphs it finished.
/// Short-circuits when the (known) source equals the target, and returns a
/// <see cref="TranslationStatus.Failed"/> result — never throws to the caller — on model/network error.
/// </summary>
public sealed class TranslationService : ITranslationService
{
    private readonly IChatClientFactory _clientFactory;
    private readonly ITranslationStore _store;
    private readonly ILogger<TranslationService> _logger;

    public TranslationService(IChatClientFactory clientFactory, ITranslationStore store, ILogger<TranslationService>? logger = null)
    {
        _clientFactory = clientFactory;
        _store = store;
        _logger = logger ?? NullLogger<TranslationService>.Instance;
    }

    public async Task<PageTranslation> TranslateAsync(
        Document document,
        Page page,
        LanguageCode target,
        LanguageCode? sourceOverride,
        ModelProfile model,
        TranslationOrigin origin,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(model);

        var source = sourceOverride ?? document.DetectedSourceLanguage;

        if (!page.HasText)
        {
            return PageTranslation.Failed(page.Index, target, model.ModelId, "This page has no text to translate.", origin);
        }

        // Source == target → no translation needed; echo the original without calling the model.
        if (!source.IsUnknown && !target.IsUnknown && string.Equals(source.Code, target.Code, StringComparison.OrdinalIgnoreCase))
        {
            return PageTranslation.Completed(page.Index, target, source, model.ModelId, page.ExtractableText, origin);
        }

        var translated = new string[page.Segments.Count];
        for (var i = 0; i < page.Segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var segment = page.Segments[i];
            var key = TranslationKey.For(segment, source, target, model.ModelId);
            if (_store.TryGet(key, out var cached))
            {
                translated[i] = cached;
                continue;
            }

            var result = await TranslateSegmentAsync(segment, source, target, model, ct).ConfigureAwait(false);
            if (result.Failure is { } failure)
            {
                return PageTranslation.Failed(page.Index, target, model.ModelId, failure, origin);
            }

            translated[i] = result.Text!;
            await _store.SaveAsync(key, result.Text!, ct).ConfigureAwait(false);
        }

        return PageTranslation.Completed(page.Index, target, source, model.ModelId, string.Join("\n\n", translated), origin);
    }

    private async Task<(string? Text, string? Failure)> TranslateSegmentAsync(
        string segment,
        LanguageCode source,
        LanguageCode target,
        ModelProfile model,
        CancellationToken ct)
    {
        try
        {
            var messages = BuildMessages(segment, source, target);
            var response = await GetResponseWithLocalLatestFallbackAsync(model, messages, ct).ConfigureAwait(false);
            var text = response.Text;

            return string.IsNullOrWhiteSpace(text)
                ? (null, "The model returned an empty translation.")
                : (text, null);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Translation request timed out with model {Model}.", model.ModelId);
            return (null, "The translation timed out. Try again.");
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a failure result — let the coordinator/VM handle it
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Translation failed with model {Model}.", model.ModelId);
            return (null, DescribeFailure(ex));
        }
    }

    private static List<ChatMessage> BuildMessages(string text, LanguageCode source, LanguageCode target)
    {
        // The page itself is untrusted and stays in its own user message — never concatenated into the
        // instructions. The language codes are interpolated, and they are untrusted too: the source is
        // sniffed from document metadata and the target is typed by hand. Both are reduced to the BCP-47
        // alphabet first, so neither can end the sentence and append instructions of its own.
        var from = PromptSafeLanguage(source) ?? "the source language";
        var to = PromptSafeLanguage(target) ?? "the target language";
        var system =
            $"You are a translation engine. Translate the user's text from {from} into {to}. " +
            "Preserve meaning, tone, and paragraph breaks. Output only the translated text with no preamble or notes.";
        return
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, text),
        ];
    }

    /// <summary>A language code reduced to what BCP-47 allows (letters, digits, hyphen), or null when
    /// nothing usable survives.</summary>
    private static string? PromptSafeLanguage(LanguageCode language)
    {
        if (language.IsUnknown)
        {
            return null;
        }

        var safe = string.Concat(language.Code.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
        return safe.Length is > 0 and <= 35 ? safe : null; // BCP-47 tags are short; anything longer is not one
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        ModelConfigurationException => ex.Message,
        HttpRequestException => "Could not reach the translation model. Check your connection or the model endpoint.",
        TimeoutException => "The translation timed out. Try again.",
        _ when DescribeMissingModelFailure(ex) is { } message => message,
        _ => "The translation failed. Try again.",
    };

    private static string? DescribeMissingModelFailure(Exception ex)
    {
        var message = ex.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        const string startMarker = "model '";
        const string endMarker = "' not found";
        var start = message.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
        var end = message.IndexOf(endMarker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end <= start + startMarker.Length)
        {
            return null;
        }

        var modelId = message.Substring(start + startMarker.Length, end - (start + startMarker.Length));
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return "The selected model is not available on the configured endpoint. Update the model in Settings.";
        }

        return $"Model '{modelId}' is not available on the configured endpoint. Pick an installed Ollama tag in Settings (for example, llama3.1:8b).";
    }

    private async Task<ChatResponse> GetResponseWithLocalLatestFallbackAsync(
        ModelProfile model,
        IEnumerable<ChatMessage> messages,
        CancellationToken ct)
    {
        using var client = _clientFactory.Create(model);
        try
        {
            return await client.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldRetryLocalModelTag(model, ex))
        {
            var retryCandidates = BuildLocalRetryModelIds(model.ModelId);
            var last = ex;
            foreach (var candidate in retryCandidates)
            {
                var fallbackModel = new ModelProfile(
                    model.ProfileId,
                    model.DisplayName,
                    model.Kind,
                    candidate,
                    model.Endpoint,
                    model.CredentialRef);
                using var fallbackClient = _clientFactory.Create(fallbackModel);
                try
                {
                    return await fallbackClient.GetResponseAsync(messages, options: null, ct).ConfigureAwait(false);
                }
                catch (Exception retryEx) when (DescribeMissingModelFailure(retryEx) is not null)
                {
                    last = retryEx;
                }
            }

            throw last;
        }
    }

    private static bool ShouldRetryLocalModelTag(ModelProfile model, Exception ex) =>
        model.Kind == ModelKind.Local
        && DescribeMissingModelFailure(ex) is not null;

    private static IReadOnlyList<string> BuildLocalRetryModelIds(string originalModelId)
    {
        var candidates = new List<string>(4);
        var baseModel = originalModelId.Split(':', 2)[0];
        if (!originalModelId.Contains(':', StringComparison.Ordinal))
        {
            candidates.Add($"{originalModelId}:latest");
            candidates.Add($"{originalModelId}:8b");
            return candidates;
        }

        candidates.Add(baseModel);
        candidates.Add($"{baseModel}:latest");
        candidates.Add($"{baseModel}:8b");
        return candidates
            .Where(c => !string.Equals(c, originalModelId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
