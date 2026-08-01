using BabelRead.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BabelRead.Core.Translation;

/// <summary>
/// Translates ahead of the reader so turns are instant on slow local models (FR-015), and then keeps going
/// until the whole book is translated — pages behind the reader, and pages skipped over, are filled in too,
/// so no part of the book is left permanently untranslated just because it was never read past.
/// The work runs on a background task with its own cancellation token: it is cancelled when the reader
/// navigates elsewhere, and it never blocks an on-demand translation because it shares no lock with one
/// (FR-016). Pages whose every segment is already in the store are skipped without touching the model.
/// </summary>
public sealed class PrefetchCoordinator : IPrefetchCoordinator
{
    /// <summary>How many pages ahead "Off" still translates, so the immediate next turns stay instant
    /// without keeping the model busy on the rest of the book.</summary>
    private const int OffReadAheadPages = 2;

    private readonly ITranslationService _translationService;
    private readonly ITranslationStore _store;
    private readonly ILogger<PrefetchCoordinator> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public PrefetchCoordinator(ITranslationService translationService, ITranslationStore store, ILogger<PrefetchCoordinator>? logger = null)
    {
        _translationService = translationService;
        _store = store;
        _logger = logger ?? NullLogger<PrefetchCoordinator>.Instance;
    }

    /// <summary>The in-flight prefetch task (or a completed task). Exposed for deterministic tests.</summary>
    public Task PendingTask { get; private set; } = Task.CompletedTask;

    private BackgroundTranslation _mode = BackgroundTranslation.Gentle;

    /// <summary>How hard to work. Turning it off abandons whatever is in flight.</summary>
    public BackgroundTranslation Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            if (value == BackgroundTranslation.Off)
            {
                CancelPending();
            }
        }
    }

    public void OnPageSettled(PrefetchContext context, int currentIndex, ReadingDirection direction)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Document.PageCount == 0)
        {
            return;
        }

        // Even on the last page there is work to do: whatever the reader skipped past.
        var nextIndex = direction == ReadingDirection.Forward ? currentIndex + 1 : currentIndex - 1;
        CancellationToken token;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            // Do not pass the token to Task.Run: if it trips before the delegate starts, the task
            // would fault as cancelled and awaiters would throw. PrefetchAsync handles cancellation
            // internally and always completes normally.
            PendingTask = Task.Run(() => PrefetchAsync(context, nextIndex, direction, token));
        }
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }

    private async Task PrefetchAsync(PrefetchContext context, int index, ReadingDirection direction, CancellationToken token)
    {
        try
        {
            foreach (var pageIndex in EnumerateWork(index, direction, context.Document.PageCount, Mode))
            {
                token.ThrowIfCancellationRequested();

                var page = await context.GetPageAsync(pageIndex, token).ConfigureAwait(false);
                if (page is null || !page.HasText)
                {
                    continue;
                }

                if (IsFullyTranslated(page, context))
                {
                    continue; // already done, in this session or a previous one
                }

                token.ThrowIfCancellationRequested();

                // The service translates and stores each missing segment; segments already held are reused.
                await _translationService
                    .TranslateAsync(context.Document, page, context.Target, context.SourceOverride, context.Model, TranslationOrigin.Prefetch, token)
                    .ConfigureAwait(false);

                // Gentle mode idles here between pages, which is what keeps the machine cool.
                var pause = BackgroundTranslationPace.InterPageDelay(Mode);
                if (pause > TimeSpan.Zero)
                {
                    await Task.Delay(pause, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Reader navigated away — expected; drop the prefetch silently.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prefetch of page {Page} failed.", index);
        }
    }

    private bool IsFullyTranslated(Page page, PrefetchContext context)
    {
        var source = context.SourceOverride ?? context.Document.DetectedSourceLanguage;
        return page.Segments.All(s => _store.Contains(TranslationKey.For(s, source, context.Target, context.Model.ModelId)));
    }

    /// <summary>
    /// The pages the reader is about to need, in the order they will be needed. In Off mode that is all it
    /// yields — just the next couple of pages. Otherwise it continues, once the read-ahead is done, through
    /// everything else in the book, so a page that was skipped over still gets translated eventually.
    /// </summary>
    private static IEnumerable<int> EnumerateWork(int startIndex, ReadingDirection direction, int pageCount, BackgroundTranslation mode)
    {
        var step = direction == ReadingDirection.Forward ? 1 : -1;

        if (mode == BackgroundTranslation.Off)
        {
            var produced = 0;
            for (var i = startIndex; i >= 0 && i < pageCount && produced < OffReadAheadPages; i += step)
            {
                produced++;
                yield return i;
            }

            yield break;
        }

        var aheadOfTheReader = new HashSet<int>();
        for (var i = startIndex; i >= 0 && i < pageCount; i += step)
        {
            aheadOfTheReader.Add(i);
            yield return i;
        }

        for (var i = 0; i < pageCount; i++)
        {
            if (!aheadOfTheReader.Contains(i))
            {
                yield return i;
            }
        }
    }
}
