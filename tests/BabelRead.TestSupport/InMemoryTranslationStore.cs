using System.Collections.Concurrent;
using BabelRead.Core.Domain;
using BabelRead.Core.Translation;

namespace BabelRead.TestSupport;

/// <summary>Translation store that keeps segments in memory — the disk behaviour is covered separately by
/// the <c>JsonTranslationStore</c> tests.</summary>
public sealed class InMemoryTranslationStore : ITranslationStore
{
    private readonly ConcurrentDictionary<TranslationKey, string> _segments = new();

    public event EventHandler? SegmentStored;

    public IReadOnlyDictionary<TranslationKey, string> Segments => _segments;

    public Task OpenAsync(string documentId, CancellationToken ct = default) => Task.CompletedTask;

    public bool TryGet(TranslationKey key, out string translatedText) => _segments.TryGetValue(key, out translatedText!);

    public bool Contains(TranslationKey key) => _segments.ContainsKey(key);

    public Task SaveAsync(TranslationKey key, string translatedText, CancellationToken ct = default)
    {
        _segments[key] = translatedText;
        SegmentStored?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ImportAsync(IReadOnlyDictionary<TranslationKey, string> segments, CancellationToken ct = default)
    {
        foreach (var (key, text) in segments)
        {
            _segments[key] = text;
        }

        SegmentStored?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public int CountStored(IEnumerable<TranslationKey> keys) => keys.Count(_segments.ContainsKey);
}
