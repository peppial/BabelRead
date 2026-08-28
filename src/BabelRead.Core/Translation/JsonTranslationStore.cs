using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BabelRead.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BabelRead.Core.Translation;

/// <summary>
/// Stores translated segments as one JSON file per book under the application data folder. Writes are
/// serialized through a single background writer and are debounced, so a burst of finished segments costs
/// one file write rather than one per segment — but nothing is ever held back longer than
/// <see cref="FlushDelayMs"/>, so quitting the app loses at most that much work.
/// </summary>
public sealed class JsonTranslationStore : ITranslationStore, IAsyncDisposable
{
    private const int FlushDelayMs = 1000;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly string _directory;
    private readonly ILogger<JsonTranslationStore> _logger;
    private readonly ConcurrentDictionary<TranslationKey, StoredSegment> _segments = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private string? _documentId;
    private volatile bool _dirty;
    private Task _writer = Task.CompletedTask;

    public JsonTranslationStore(string? directory = null, ILogger<JsonTranslationStore>? logger = null)
    {
        _directory = directory ?? DefaultDirectory();
        _logger = logger ?? NullLogger<JsonTranslationStore>.Instance;
    }

    public event EventHandler? SegmentStored;

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "BabelRead",
        "translations");

    /// <summary>File this document's translations live in — a hash of its id, so any path is a legal name.
    /// The readable prefix is built from an allowlist rather than <see cref="Path.GetInvalidFileNameChars"/>:
    /// that set is platform-specific (on Unix it is only '/' and NUL), so a Windows-style id such as
    /// <c>..\..\x</c> would keep its separators and traverse once the same file is written on Windows.</summary>
    public string FilePathFor(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentId.ToLowerInvariant())))[..16].ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(documentId);
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '_' ? c : '-'));
        safe = safe.Length > 60 ? safe[..60] : safe;
        safe = safe.Trim(' ', '.', '-');
        return Path.Combine(_directory, $"{safe}-{hash}.json".TrimStart('-'));
    }

    public async Task OpenAsync(string documentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        await FlushAsync().ConfigureAwait(false); // don't lose the previous book's tail

        _segments.Clear();
        _documentId = documentId;

        var path = FilePathFor(documentId);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<TranslationFile>(stream, Options, ct).ConfigureAwait(false);
            foreach (var entry in file?.Segments ?? [])
            {
                if (string.IsNullOrWhiteSpace(entry.TextHash) || string.IsNullOrWhiteSpace(entry.ModelId) || entry.Text is null)
                {
                    continue;
                }

                var key = new TranslationKey(entry.TextHash, new LanguageCode(entry.SourceLanguage), new LanguageCode(entry.TargetLanguage), entry.ModelId);
                _segments[key] = entry;
            }
        }
        catch (Exception ex)
        {
            // A damaged file must not stop the reader — worst case the book translates again.
            _logger.LogWarning(ex, "Could not read translations for {Document}; starting empty.", documentId);
            _segments.Clear();
        }
    }

    public bool TryGet(TranslationKey key, out string translatedText)
    {
        if (_segments.TryGetValue(key, out var segment))
        {
            translatedText = segment.Text ?? string.Empty;
            return true;
        }

        translatedText = string.Empty;
        return false;
    }

    public bool Contains(TranslationKey key) => _segments.ContainsKey(key);

    public Task SaveAsync(TranslationKey key, string translatedText, CancellationToken ct = default)
    {
        Add(key, translatedText);
        SegmentStored?.Invoke(this, EventArgs.Empty);
        ScheduleWrite();
        return Task.CompletedTask;
    }

    public Task ImportAsync(IReadOnlyDictionary<TranslationKey, string> segments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var (key, text) in segments)
        {
            Add(key, text);
        }

        SegmentStored?.Invoke(this, EventArgs.Empty);
        ScheduleWrite();
        return Task.CompletedTask;
    }

    public int CountStored(IEnumerable<TranslationKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.Count(_segments.ContainsKey);
    }

    /// <summary>Waits for pending translations to reach disk.</summary>
    public async Task FlushAsync()
    {
        await _writer.ConfigureAwait(false);
        if (_dirty)
        {
            await WriteAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await FlushAsync().ConfigureAwait(false);

    private void Add(TranslationKey key, string translatedText)
    {
        _segments[key] = new StoredSegment
        {
            TextHash = key.TextHash,
            TargetLanguage = key.TargetLanguage.Code,
            SourceLanguage = key.SourceLanguage.Code,
            ModelId = key.ModelId,
            Text = translatedText,
        };
        _dirty = true;
    }

    private void ScheduleWrite()
    {
        if (!_writer.IsCompleted)
        {
            return; // a write is already pending; it will pick this segment up
        }

        _writer = Task.Run(async () =>
        {
            await Task.Delay(FlushDelayMs).ConfigureAwait(false);
            await WriteAsync().ConfigureAwait(false);
        });
    }

    private async Task WriteAsync()
    {
        var documentId = _documentId;
        if (documentId is null)
        {
            return;
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _dirty = false;
            var path = FilePathFor(documentId);
            Directory.CreateDirectory(_directory);

            var file = new TranslationFile { DocumentId = documentId, Segments = _segments.Values.ToList() };
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, Options).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true); // atomic swap — a crash mid-write cannot corrupt the store
        }
        catch (Exception ex)
        {
            _dirty = true;
            _logger.LogWarning(ex, "Could not persist translations for {Document}.", documentId);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed class TranslationFile
    {
        public string DocumentId { get; set; } = string.Empty;

        public List<StoredSegment> Segments { get; set; } = [];
    }

    private sealed class StoredSegment
    {
        public string TextHash { get; set; } = string.Empty;

        public string TargetLanguage { get; set; } = string.Empty;

        public string SourceLanguage { get; set; } = string.Empty;

        public string ModelId { get; set; } = string.Empty;

        public string? Text { get; set; }
    }
}
