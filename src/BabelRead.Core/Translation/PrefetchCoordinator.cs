using BabelRead.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BabelRead.Core.Translation;

/// <summary>
/// Prefetches translation in the reading direction and stores it in the cache, so turns are instant on
/// slow local models (FR-015). The prefetch runs on a background task with its own cancellation token:
/// it is cancelled when the reader navigates elsewhere, and it never blocks an on-demand translation
/// because it shares no lock with one (FR-016).
/// </summary>
public sealed class PrefetchCoordinator : IPrefetchCoordinator
{
    // Keep background translation very low priority to avoid heating/CPU pressure on local machines.
    private static readonly TimeSpan InterPageThrottle = TimeSpan.FromSeconds(10);

    private readonly ITranslationService _translationService;
    private readonly ITranslationCache _cache;
    private readonly ILogger<PrefetchCoordinator> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public PrefetchCoordinator(ITranslationService translationService, ITranslationCache cache, ILogger<PrefetchCoordinator>? logger = null)
    {
        _translationService = translationService;
        _cache = cache;
        _logger = logger ?? NullLogger<PrefetchCoordinator>.Instance;
    }

    /// <summary>The in-flight prefetch task (or a completed task). Exposed for deterministic tests.</summary>
    public Task PendingTask { get; private set; } = Task.CompletedTask;

    public void OnPageSettled(PrefetchContext context, int currentIndex, ReadingDirection direction)
    {
        ArgumentNullException.ThrowIfNull(context);

        var nextIndex = direction == ReadingDirection.Forward ? currentIndex + 1 : currentIndex - 1;
        if (nextIndex < 0 || nextIndex >= context.Document.PageCount)
        {
            return;
        }

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
            foreach (var pageIndex in EnumerateWork(index, direction, context.Document.PageCount))
            {
                token.ThrowIfCancellationRequested();
                var key = new TranslationKey(context.Document.Id, pageIndex, context.Target, context.Model.ModelId);
                if (_cache.TryGet(key, out _))
                {
                    continue; // already translated
                }

                var page = await context.GetPageAsync(pageIndex, token).ConfigureAwait(false);
                if (page is null)
                {
                    continue;
                }

                token.ThrowIfCancellationRequested();

                var result = await _translationService
                    .TranslateAsync(context.Document, page, context.Target, context.SourceOverride, context.Model, TranslationOrigin.Prefetch, token)
                    .ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                if (result.Status == TranslationStatus.Completed)
                {
                    _cache.Set(key, result);
                }

                // Keep background pretranslation polite so local models don't monopolize CPU.
                await Task.Delay(InterPageThrottle, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Reader navigated away — expected; drop the prefetch silently.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Prefetch of page {Page} failed.", index);
        }
    }

    private static IEnumerable<int> EnumerateWork(int startIndex, ReadingDirection direction, int pageCount)
    {
        if (direction == ReadingDirection.Forward)
        {
            for (var i = startIndex; i < pageCount; i++)
            {
                yield return i;
            }

            yield break;
        }

        for (var i = startIndex; i >= 0; i--)
        {
            yield return i;
        }
    }
}
