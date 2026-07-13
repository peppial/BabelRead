using BabelRead.Core.Domain;

namespace BabelRead.Core.Translation;

/// <summary>Direction the reader is travelling; determines which neighbouring page to prefetch.</summary>
public enum ReadingDirection
{
    Forward,
    Backward,
}

/// <summary>
/// Context a prefetch needs to translate the next page — the same inputs an on-demand translation uses,
/// plus a way to fetch a page's text without coupling the coordinator to a specific document reader.
/// </summary>
public sealed record PrefetchContext(
    Document Document,
    LanguageCode Target,
    LanguageCode? SourceOverride,
    ModelProfile Model,
    Func<int, CancellationToken, Task<Page?>> GetPageAsync);

/// <summary>
/// Drives background translation in the reading direction so as many upcoming pages as possible are
/// ready in cache (FR-015). Must cancel pending work when the reader moves elsewhere and must never
/// delay an on-demand translation (FR-016).
/// </summary>
public interface IPrefetchCoordinator
{
    /// <summary>Schedule background pretranslation starting from the adjacent page in <paramref name="direction"/>.</summary>
    void OnPageSettled(PrefetchContext context, int currentIndex, ReadingDirection direction);

    /// <summary>Cancel any in-flight prefetch (call on navigation change / document close).</summary>
    void CancelPending();
}
