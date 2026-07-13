using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BabelRead.Core.Translation;

/// <summary>
/// Translates one page into the target language via the active model client. Short-circuits when the
/// (known) source equals the target, and returns a <see cref="TranslationStatus.Failed"/> result — never
/// throws to the caller — on model/network error. The result's <see cref="PageTranslation.PageIndex"/>
/// always matches the source page (FR-010).
/// </summary>
public sealed class TranslationService : ITranslationService
{
    private readonly IChatClientFactory _clientFactory;
    private readonly ILogger<TranslationService> _logger;

    public TranslationService(IChatClientFactory clientFactory, ILogger<TranslationService>? logger = null)
    {
        _clientFactory = clientFactory;
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

        try
        {
            var messages = BuildMessages(page.ExtractableText, source, target);
            var response = await GetResponseWithLocalLatestFallbackAsync(model, messages, ct).ConfigureAwait(false);
            var text = response.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                return PageTranslation.Failed(page.Index, target, model.ModelId, "The model returned an empty translation.", origin);
            }

            return PageTranslation.Completed(page.Index, target, source, model.ModelId, text, origin);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Translation request timed out for page {Page} with model {Model}.", page.Index, model.ModelId);
            return PageTranslation.Failed(page.Index, target, model.ModelId, "The translation timed out. Try again.", origin);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a failure result — let the coordinator/VM handle it
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Translation failed for page {Page} with model {Model}.", page.Index, model.ModelId);
            return PageTranslation.Failed(page.Index, target, model.ModelId, DescribeFailure(ex), origin);
        }
    }

    private static List<ChatMessage> BuildMessages(string text, LanguageCode source, LanguageCode target)
    {
        var from = source.IsUnknown ? "the source language" : source.Code;
        var system =
            $"You are a translation engine. Translate the user's text from {from} into {target.Code}. " +
            "Preserve meaning, tone, and paragraph breaks. Output only the translated text with no preamble or notes.";
        return
        [
            new ChatMessage(ChatRole.System, system),
            new ChatMessage(ChatRole.User, text),
        ];
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
