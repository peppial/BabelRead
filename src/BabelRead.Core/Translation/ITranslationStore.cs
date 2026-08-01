using BabelRead.Core.Domain;

namespace BabelRead.Core.Translation;

/// <summary>
/// Durable, content-addressed home for translated segments — one file per book, written as each segment
/// completes. Because segments are keyed by the hash of their source text (never by page index), nothing
/// stored here is invalidated by repagination, a font change, or a window resize.
/// </summary>
public interface ITranslationStore
{
    /// <summary>Raised after a segment is added, on whatever thread produced it.</summary>
    event EventHandler? SegmentStored;

    /// <summary>Loads the translations held for a document. Call on open, before reading.</summary>
    Task OpenAsync(string documentId, CancellationToken ct = default);

    bool TryGet(TranslationKey key, out string translatedText);

    bool Contains(TranslationKey key);

    /// <summary>Adds a segment translation and persists it.</summary>
    Task SaveAsync(TranslationKey key, string translatedText, CancellationToken ct = default);

    /// <summary>How many of <paramref name="keys"/> are already translated — the progress numerator.</summary>
    int CountStored(IEnumerable<TranslationKey> keys);

    /// <summary>Adds segments produced elsewhere (used to migrate the old page-keyed cache).</summary>
    Task ImportAsync(IReadOnlyDictionary<TranslationKey, string> segments, CancellationToken ct = default);
}
