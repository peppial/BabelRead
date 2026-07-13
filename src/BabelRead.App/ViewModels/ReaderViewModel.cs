using BabelRead.Core.Documents;
using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using BabelRead.Core.Preferences;
using BabelRead.Core.Translation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq;
using System.Threading;

namespace BabelRead.App.ViewModels;

/// <summary>
/// Orchestrates reading and on-the-fly translation: opening a document, page navigation, the
/// original/translation toggle, progress/error states, and next-page prefetch. All translation runs
/// off the UI thread; a per-page cancellation token ensures a stale result never lands on the wrong
/// page (FR-010) and that navigating away abandons in-flight work.
/// </summary>
public sealed partial class ReaderViewModel : ObservableObject
{
    private readonly DocumentReaderRegistry _readers;
    private readonly ITranslationService _translation;
    private readonly ITranslationCache _cache;
    private readonly IPrefetchCoordinator _prefetch;
    private readonly IPreferencesStore _preferences;
    private readonly ILogger<ReaderViewModel> _logger;

    private IDocumentReader? _reader;
    private Document? _document;
    private ModelProfile _model = ModelProfiles.DefaultLocal();
    private LanguageCode _target = new("en");
    private LanguageCode? _sourceOverride;
    private int _currentIndex;
    private double _viewportWidth;
    private double _viewportHeight;
    private CancellationTokenSource? _pageCts;
    private readonly Dictionary<string, PageTranslation> _persistedTranslations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private bool _suppressPersistenceFromCacheEvents;

    [ObservableProperty]
    private string _title = "BabelRead";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageLabel))]
    private int _pageNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageLabel))]
    private int _pageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string? _originalText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private string? _translationText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(ReadingFlowDirection))]
    private bool _showingTranslation = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    private ReaderState _state = ReaderState.NoDocument;

    [ObservableProperty]
    private string? _statusMessage = "Open a PDF or EPUB to begin.";

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslationPercentLabel))]
    private int _translatedPages;

    [ObservableProperty]
    private double _translationProgressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingLineHeight))]
    private double _readingFontSize = ReadingFontSizes.Default;

    public ReaderViewModel(
        DocumentReaderRegistry readers,
        ITranslationService translation,
        ITranslationCache cache,
        IPrefetchCoordinator prefetch,
        IPreferencesStore preferences,
        ILogger<ReaderViewModel>? logger = null)
    {
        _readers = readers;
        _translation = translation;
        _cache = cache;
        _prefetch = prefetch;
        _preferences = preferences;
        _logger = logger ?? NullLogger<ReaderViewModel>.Instance;
        _cache.EntryStored += OnCacheEntryStored;
    }

    /// <summary>Text shown in the reading pane, honouring the original/translation toggle (FR-013).</summary>
    public string? DisplayText => ShowingTranslation ? TranslationText : OriginalText;

    /// <summary>Right-to-left when showing a translation into an RTL language (Arabic, Hebrew, ...).</summary>
    public Avalonia.Media.FlowDirection ReadingFlowDirection =>
        ShowingTranslation && _target.IsRightToLeft
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;

    /// <summary>Line height that keeps the reading pane legible as the font zooms.</summary>
    public double ReadingLineHeight => Math.Round(ReadingFontSize * 1.45);

    /// <summary>Label for the toggle control.</summary>
    public string ToggleLabel => ShowingTranslation ? "Show original" : "Show translation";

    public bool IsContentVisible => State == ReaderState.Content;

    public bool IsStatusVisible => State is ReaderState.Loading or ReaderState.NoText or ReaderState.Error or ReaderState.NoDocument;

    public bool ShowRetry => State == ReaderState.Error;

    public string CurrentPageLabel => $"Page {PageNumber}/{PageCount}";

    public string TranslationPercentLabel =>
        $"{(PageCount <= 0 || TranslatedPages <= 0 ? 0 : Math.Clamp((int)Math.Ceiling((double)TranslatedPages * 100d / PageCount), 1, 100))}% translated";

    /// <summary>The active model profile (updated by Settings in US2); later translations use it.</summary>
    public ModelProfile ActiveModel
    {
        get => _model;
        set
        {
            _model = value;
            UpdateTranslationProgress();
        }
    }

    /// <summary>Reader-selected target language (US3).</summary>
    public LanguageCode TargetLanguage
    {
        get => _target;
        set
        {
            _target = value;
            UpdateTranslationProgress();
        }
    }

    /// <summary>Optional source-language override for the current document (US3).</summary>
    public LanguageCode? SourceLanguageOverride
    {
        get => _sourceOverride;
        set => _sourceOverride = value;
    }

    /// <summary>Set the target language, persist it, and re-translate the current page (US3, FR-006).</summary>
    public async Task SetTargetLanguageAsync(LanguageCode target)
    {
        if (target.IsUnknown || target.Code == _target.Code)
        {
            return;
        }

        _target = target;
        await UpdatePreferencesAsync(prefs => prefs.TargetLanguage = target).ConfigureAwait(true);

        if (_document is not null)
        {
            await GoToPageAsync(_currentIndex, ReadingDirection.Forward).ConfigureAwait(true);
        }
    }

    /// <summary>Override the detected source language for the current document, persist it, and
    /// re-translate (US3, FR-006).</summary>
    public async Task SetSourceOverrideAsync(LanguageCode? source)
    {
        _sourceOverride = source is { IsUnknown: false } ? source : null;

        if (_document is not null)
        {
            await UpdatePreferencesAsync(
                prefs =>
                {
                    LanguageResolver.SetOverride(prefs, _document.Id, _sourceOverride);
                    prefs.TranslationCacheByDocument.Remove(_document.Id); // source changes invalidate stored translations for this book
                }).ConfigureAwait(true);

            // The source language affects every page, and the cache key does not include it, so
            // invalidate cached translations before re-translating.
            _cache.Clear();
            _persistedTranslations.Clear();
            _prefetch.CancelPending();
            await GoToPageAsync(_currentIndex, ReadingDirection.Forward).ConfigureAwait(true);
        }
    }

    /// <summary>Loads persisted preferences (target language, default toggle). Call once at startup.</summary>
    public async Task InitializeAsync()
    {
        var prefs = await LoadPreferencesAsync().ConfigureAwait(true);
        if (!prefs.TargetLanguage.IsUnknown)
        {
            _target = prefs.TargetLanguage;
        }

        ShowingTranslation = prefs.PaneToggleDefault == PaneView.Translation;
        ReadingFontSize = ReadingFontSizes.Clamp(prefs.ReadingFontSize);
        if (!string.IsNullOrWhiteSpace(prefs.LastOpenedDocumentPath) && File.Exists(prefs.LastOpenedDocumentPath))
        {
            await OpenInternalAsync(prefs.LastOpenedDocumentPath, restoreLastReadPage: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public Task OpenAsync(string path) => OpenInternalAsync(path, restoreLastReadPage: false);

    public async Task ReflowForViewportAsync(double viewportWidth, double viewportHeight)
    {
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;

        if (_document is null || _reader is null)
        {
            return;
        }

        // A larger font fits less text, so shrink the viewport the pagination heuristic sees.
        var fontScale = ReadingFontSizes.PaginationBaseline / ReadingFontSize;
        if (_reader is not IReflowableDocumentReader reflowable
            || !reflowable.UpdateViewport(viewportWidth * fontScale, viewportHeight * fontScale))
        {
            return;
        }

        // Keep reading position approximately stable after reflow by mapping by progress ratio.
        var previousPageCount = Math.Max(1, _document.PageCount);
        var previousIndex = _currentIndex;
        _document = await _reader.OpenAsync(_document.SourcePath, CancellationToken.None).ConfigureAwait(true);
        PageCount = _document.PageCount;
        var ratio = (double)previousIndex / previousPageCount;
        var mappedIndex = Math.Clamp((int)Math.Round(ratio * _document.PageCount, MidpointRounding.AwayFromZero), 0, Math.Max(0, _document.PageCount - 1));
        await GoToPageAsync(mappedIndex, ReadingDirection.Forward).ConfigureAwait(true);
    }

    private async Task OpenInternalAsync(string path, bool restoreLastReadPage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _prefetch.CancelPending();
        (_reader as IDisposable)?.Dispose();
        _cache.Clear();
        _persistedTranslations.Clear();
        UpdateTranslationProgress();

        try
        {
            _reader = _readers.ResolveFor(path);
            _document = await _reader.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            Title = _document.Title;
            PageCount = _document.PageCount;
            var prefs = await UpdatePreferencesAsync(p => p.LastOpenedDocumentPath = path).ConfigureAwait(true);
            _sourceOverride = LanguageResolver.GetOverride(prefs, _document.Id);
            LoadPersistedTranslations(_document, prefs);
            UpdateTranslationProgress();
            var startIndex = restoreLastReadPage && prefs.LastReadPageByDocument.TryGetValue(_document.Id, out var savedIndex)
                ? Math.Clamp(savedIndex, 0, _document.PageCount - 1)
                : 0;
            await GoToPageAsync(startIndex, ReadingDirection.Forward).ConfigureAwait(true);
        }
        catch (DocumentOpenException ex)
        {
            _document = null;
            State = ReaderState.Error;
            StatusMessage = ex.Message;
            _logger.LogWarning(ex, "Failed to open document.");
        }
    }

    [RelayCommand]
    public Task NextPageAsync() =>
        _document is null || _currentIndex + 1 >= _document.PageCount
            ? Task.CompletedTask
            : GoToPageAsync(_currentIndex + 1, ReadingDirection.Forward);

    [RelayCommand]
    public Task PreviousPageAsync() =>
        _document is null || _currentIndex - 1 < 0
            ? Task.CompletedTask
            : GoToPageAsync(_currentIndex - 1, ReadingDirection.Backward);

    /// <summary>Jump to a 1-based page number.</summary>
    public Task JumpToPageAsync(int oneBasedPageNumber)
    {
        if (_document is null)
        {
            return Task.CompletedTask;
        }

        var index = Math.Clamp(oneBasedPageNumber - 1, 0, _document.PageCount - 1);
        var direction = index >= _currentIndex ? ReadingDirection.Forward : ReadingDirection.Backward;
        return GoToPageAsync(index, direction);
    }

    [RelayCommand]
    public void ToggleView() => ShowingTranslation = !ShowingTranslation;

    /// <summary>Ctrl+ — grow the reading font one step (FR-013 readability).</summary>
    [RelayCommand]
    public Task IncreaseFontSizeAsync() => SetFontSizeAsync(ReadingFontSize + ReadingFontSizes.Step);

    /// <summary>Ctrl- — shrink the reading font one step.</summary>
    [RelayCommand]
    public Task DecreaseFontSizeAsync() => SetFontSizeAsync(ReadingFontSize - ReadingFontSizes.Step);

    private async Task SetFontSizeAsync(double size)
    {
        var clamped = ReadingFontSizes.Clamp(size);
        if (Math.Abs(clamped - ReadingFontSize) < 0.01)
        {
            return; // already at the limit
        }

        ReadingFontSize = clamped;
        await UpdatePreferencesAsync(prefs => prefs.ReadingFontSize = clamped).ConfigureAwait(true);

        if (_viewportWidth > 0 && _viewportHeight > 0)
        {
            await ReflowForViewportAsync(_viewportWidth, _viewportHeight).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public Task RetryAsync() =>
        _document is null ? Task.CompletedTask : GoToPageAsync(_currentIndex, ReadingDirection.Forward);

    private async Task GoToPageAsync(int index, ReadingDirection direction)
    {
        if (_document is null || _reader is null)
        {
            return;
        }

        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = new CancellationTokenSource();
        var token = _pageCts.Token;

        _currentIndex = index;
        PageNumber = index + 1;
        UpdateNavigation();
        State = ReaderState.Loading;
        StatusMessage = "Translating…";

        try
        {
            var page = await _reader.GetPageAsync(_document, index, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            OriginalText = page.ExtractableText;
            await PersistLastReadPageAsync(page.Index).ConfigureAwait(true);

            if (!page.HasText)
            {
                TranslationText = null;
                State = ReaderState.NoText;
                StatusMessage = "This page has no text to translate.";
            }
            else
            {
                await TranslateCurrentAsync(page, token).ConfigureAwait(true);
            }

            SchedulePrefetch(direction);
        }
        catch (OperationCanceledException)
        {
            // Navigated away before this page finished — expected.
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                State = ReaderState.Error;
                StatusMessage = "Could not load this page. Try again.";
                _logger.LogWarning(ex, "Failed to load page {Index}.", index);
            }
        }
    }

    private async Task TranslateCurrentAsync(Page page, CancellationToken token)
    {
        var key = new TranslationKey(_document!.Id, page.Index, _target, _model.ModelId);
        if (_cache.TryGet(key, out var cached))
        {
            ApplyTranslation(cached, page.Index, token);
            return;
        }

        if (TryGetPersistedTranslation(key, out var persisted))
        {
            CacheWithoutPersisting(persisted, key);
            ApplyTranslation(persisted, page.Index, token);
            return;
        }

        var result = await _translation
            .TranslateAsync(_document, page, _target, _sourceOverride, _model, TranslationOrigin.OnDemand, token)
            .ConfigureAwait(true);

        if (token.IsCancellationRequested || result.PageIndex != _currentIndex)
        {
            return; // stale — belongs to a page we've navigated away from (FR-010)
        }

        if (result.Status == TranslationStatus.Completed)
        {
            _cache.Set(key, result);
        }

        ApplyTranslation(result, page.Index, token);
    }

    private void ApplyTranslation(PageTranslation result, int pageIndex, CancellationToken token)
    {
        if (token.IsCancellationRequested || pageIndex != _currentIndex)
        {
            return;
        }

        if (result.Status == TranslationStatus.Completed)
        {
            TranslationText = result.Text;
            State = ReaderState.Content;
            StatusMessage = null;
        }
        else
        {
            TranslationText = null;
            State = ReaderState.Error;
            StatusMessage = result.FailureReason ?? "Translation failed. Try again.";
        }
    }

    private void SchedulePrefetch(ReadingDirection direction)
    {
        if (_document is null || _reader is null)
        {
            return;
        }

        var reader = _reader;
        var document = _document;
        var context = new PrefetchContext(
            document,
            _target,
            _sourceOverride,
            _model,
            async (i, ct) => await reader.GetPageAsync(document, i, ct).ConfigureAwait(false));
        _prefetch.OnPageSettled(context, _currentIndex, direction);
    }

    private void UpdateNavigation()
    {
        CanGoPrevious = _document is not null && _currentIndex > 0;
        CanGoNext = _document is not null && _currentIndex + 1 < _document.PageCount;
    }

    private void OnCacheEntryStored(object? sender, TranslationCachedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        if (!string.Equals(e.Key.DocumentId, _document.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!_suppressPersistenceFromCacheEvents && e.Value.Status == TranslationStatus.Completed)
        {
            PersistCompletedTranslationAsync(e.Value).GetAwaiter().GetResult();
        }

        UpdateTranslationProgress();
    }

    private void UpdateTranslationProgress()
    {
        if (_document is null || PageCount <= 0)
        {
            TranslatedPages = 0;
            TranslationProgressPercent = 0;
            return;
        }

        var inMemoryCount = _cache.CountForDocument(_document.Id, _target, _model.ModelId);
        var persistedCount = _persistedTranslations.Values.Count(t =>
            string.Equals(t.ModelId, _model.ModelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.TargetLanguage.Code, _target.Code, StringComparison.OrdinalIgnoreCase));
        var completed = Math.Max(inMemoryCount, persistedCount);
        TranslatedPages = Math.Clamp(completed, 0, PageCount);
        TranslationProgressPercent = PageCount == 0 ? 0 : (double)TranslatedPages * 100d / PageCount;
    }

    private void LoadPersistedTranslations(Document document, ReaderPreferences preferences)
    {
        _persistedTranslations.Clear();
        if (!preferences.TranslationCacheByDocument.TryGetValue(document.Id, out var entries))
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.PageIndex < 0 || entry.PageIndex >= document.PageCount || string.IsNullOrWhiteSpace(entry.ModelId))
            {
                continue;
            }

            var target = new LanguageCode(entry.TargetLanguage);
            if (target.IsUnknown)
            {
                continue;
            }

            var source = string.IsNullOrWhiteSpace(entry.SourceLanguage) ? LanguageCode.Unknown : new LanguageCode(entry.SourceLanguage);
            var key = new TranslationKey(document.Id, entry.PageIndex, target, entry.ModelId);
            var translation = PageTranslation.Completed(entry.PageIndex, target, source, entry.ModelId, entry.Text ?? string.Empty, TranslationOrigin.OnDemand);
            _persistedTranslations[PersistedKey(key)] = translation;
        }
    }

    private async Task PersistCompletedTranslationAsync(PageTranslation result)
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            await UpdatePreferencesAsync(
                prefs =>
                {
                    if (!prefs.TranslationCacheByDocument.TryGetValue(_document.Id, out var entries))
                    {
                        entries = new List<StoredTranslation>();
                        prefs.TranslationCacheByDocument[_document.Id] = entries;
                    }

                    var existing = entries.FindIndex(e =>
                        e.PageIndex == result.PageIndex
                        && string.Equals(e.TargetLanguage, result.TargetLanguage.Code, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.ModelId, result.ModelId, StringComparison.OrdinalIgnoreCase));

                    var stored = new StoredTranslation
                    {
                        PageIndex = result.PageIndex,
                        TargetLanguage = result.TargetLanguage.Code,
                        SourceLanguage = result.SourceLanguage.Code,
                        ModelId = result.ModelId,
                        Text = result.Text,
                    };

                    if (existing >= 0)
                    {
                        entries[existing] = stored;
                    }
                    else
                    {
                        entries.Add(stored);
                    }

                    // Keep the on-disk cache bounded per document.
                    const int maxEntriesPerDocument = 5000;
                    if (entries.Count > maxEntriesPerDocument)
                    {
                        entries.RemoveRange(0, entries.Count - maxEntriesPerDocument);
                    }
                }).ConfigureAwait(false);

            var key = new TranslationKey(_document.Id, result.PageIndex, result.TargetLanguage, result.ModelId);
            _persistedTranslations[PersistedKey(key)] = result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist translation cache for document {Document}.", _document.Id);
        }
    }

    private bool TryGetPersistedTranslation(TranslationKey key, out PageTranslation translation) =>
        _persistedTranslations.TryGetValue(PersistedKey(key), out translation!);

    private static string PersistedKey(TranslationKey key) =>
        $"{key.PageIndex}|{key.TargetLanguage.Code.ToLowerInvariant()}|{key.ModelId.ToLowerInvariant()}";

    private void CacheWithoutPersisting(PageTranslation translation, TranslationKey key)
    {
        _suppressPersistenceFromCacheEvents = true;
        try
        {
            _cache.Set(key, translation);
        }
        finally
        {
            _suppressPersistenceFromCacheEvents = false;
        }
    }

    private async Task PersistLastReadPageAsync(int pageIndex)
    {
        if (_document is null)
        {
            return;
        }

        try
        {
            await UpdatePreferencesAsync(
                prefs =>
                {
                    prefs.LastOpenedDocumentPath = _document.SourcePath;
                    prefs.LastReadPageByDocument[_document.Id] = pageIndex;
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist last read page for document {Document}.", _document.Id);
        }
    }

    private async Task<ReaderPreferences> LoadPreferencesAsync(CancellationToken ct = default)
    {
        await _preferencesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _preferences.LoadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _preferencesGate.Release();
        }
    }

    private async Task<ReaderPreferences> UpdatePreferencesAsync(Action<ReaderPreferences> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _preferencesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var prefs = await _preferences.LoadAsync(ct).ConfigureAwait(false);
            update(prefs);
            await _preferences.SaveAsync(prefs, ct).ConfigureAwait(false);
            return prefs;
        }
        finally
        {
            _preferencesGate.Release();
        }
    }
}
