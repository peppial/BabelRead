using System.Diagnostics.CodeAnalysis;
using System.Linq;
using BabelRead.Core.Domain;

namespace BabelRead.Core.Translation;

/// <summary>
/// Thread-safe, session-scoped translation cache with bounded size and LRU eviction (Constitution IV).
/// Keys include target language and model id, so switching either never serves a stale entry.
/// </summary>
public sealed class TranslationCache : ITranslationCache
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<TranslationKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru = new();

    public TranslationCache(int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _map = new Dictionary<TranslationKey, LinkedListNode<Entry>>(capacity);
    }

    public event EventHandler<TranslationCachedEventArgs>? EntryStored;

    public bool TryGet(TranslationKey key, [NotNullWhen(true)] out PageTranslation? value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public void Set(TranslationKey key, PageTranslation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var shouldNotify = false;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = new Entry(key, value);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                shouldNotify = true;
            }
            else
            {
                var node = new LinkedListNode<Entry>(new Entry(key, value));
                _lru.AddFirst(node);
                _map[key] = node;

                if (_map.Count > _capacity)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                }

                shouldNotify = true;
            }
        }

        if (shouldNotify)
        {
            EntryStored?.Invoke(this, new TranslationCachedEventArgs(key, value));
        }
    }

    public int CountForDocument(string documentId, LanguageCode targetLanguage, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        lock (_gate)
        {
            return _map.Keys.Count(k =>
                string.Equals(k.DocumentId, documentId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.TargetLanguage.Code, targetLanguage.Code, StringComparison.OrdinalIgnoreCase)
                && string.Equals(k.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
        }
    }

    private readonly record struct Entry(TranslationKey Key, PageTranslation Value);
}
