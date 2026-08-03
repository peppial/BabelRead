using BabelRead.App.Reading;
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
    /// <summary>Fallback reading surface, used only before the view has reported its real size.</summary>
    private const double BaselineLayoutWidth = 1280;
    private const double BaselineLayoutHeight = 800;

    private readonly DocumentReaderRegistry _readers;
    private readonly ITranslationService _translation;
    private readonly ITranslationStore _store;
    private readonly IPrefetchCoordinator _prefetch;
    private readonly IPreferencesStore _preferences;
    private readonly ILogger<ReaderViewModel> _logger;

    private IDocumentReader? _reader;
    private Document? _document;
    private ModelProfile _model = ModelProfiles.DefaultLocal();
    private LanguageCode _target = new("en");
    private LanguageCode? _sourceOverride;
    private int _currentIndex;
    private CancellationTokenSource? _pageCts;
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    // Continuous reading model: the whole document is rendered as one flow so every screen fills top to
    // bottom and a paragraph runs on across the page break (like a printed book). Core "pages" survive only
    // as translation/navigation anchors — turning a page scrolls this flow to that page's first paragraph.
    private IReadOnlyList<string> _orderedSegments = [];      // every paragraph, in reading order
    private int[] _segmentCharOffsets = [];                    // where each paragraph starts in DisplayText
    private int[] _pageFirstSegment = [];                      // first paragraph index of each Core page

    // Visual pagination: cuts DisplayText into viewport-sized pages at the view's reported size/font.
    private readonly ReadingPaginator _paginator = new();
    private ReadingPageMetrics? _metrics;
    private int _pageStartOffset;
    private readonly Stack<int> _visitedPageStarts = new();  // page starts we can pop straight back to

    // Internal hyperlinks (EPUB only; empty for PDF): followed in the original view, with a browser-style
    // Back stack separate from ordinary page navigation.
    private IReadOnlyList<DocumentLink> _links = [];
    private IReadOnlyDictionary<string, LinkTarget> _anchors = new Dictionary<string, LinkTarget>();
    private readonly Stack<int> _linkReturnStack = new();

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
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTranslatingFallbackVisible))]
    private string? _originalText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTranslatingFallbackVisible))]
    private string? _translationText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    [NotifyPropertyChangedFor(nameof(ReadingFlowDirection))]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsTranslatingFallbackVisible))]
    [NotifyPropertyChangedFor(nameof(IsTranslationFailedVisible))]
    private bool _showingTranslation = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatusVisible))]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    [NotifyPropertyChangedFor(nameof(IsTranslatingFallbackVisible))]
    [NotifyPropertyChangedFor(nameof(IsTranslationFailedVisible))]
    private ReaderState _state = ReaderState.NoDocument;

    [ObservableProperty]
    private string? _statusMessage = "Open a PDF or EPUB to begin.";

    /// <summary>The current page's translation failed; the reader falls back to the original text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranslationFailedVisible))]
    private bool _translationFailed;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private bool _canGoPrevious;

    /// <summary>Segments of this book translated into the active language with the active model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslationPercentLabel))]
    private int _translatedSegments;

    /// <summary>Segments in the book — the denominator behind the percentage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslationPercentLabel))]
    private int _totalSegments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslationPercentLabel))]
    private double _translationProgressPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingLineHeight))]
    private double _readingFontSize = ReadingFontSizes.Default;

    /// <summary>How hard the background translator may work — the reader's heat/speed dial.</summary>
    [ObservableProperty]
    private BackgroundTranslation _backgroundTranslation = BackgroundTranslation.Gentle;

    public ReaderViewModel(
        DocumentReaderRegistry readers,
        ITranslationService translation,
        ITranslationStore store,
        IPrefetchCoordinator prefetch,
        IPreferencesStore preferences,
        ILogger<ReaderViewModel>? logger = null)
    {
        _readers = readers;
        _translation = translation;
        _store = store;
        _prefetch = prefetch;
        _preferences = preferences;
        _logger = logger ?? NullLogger<ReaderViewModel>.Instance;
        _store.SegmentStored += OnSegmentStored;
    }

    /// <summary>The whole document as one continuous flow — each paragraph translated where the store holds
    /// it, shown in the original otherwise — so the reading pane always fills top to bottom and a paragraph
    /// runs on across page breaks (FR-013). Rebuilt as translations land and when the toggle flips.</summary>
    [ObservableProperty]
    private string? _displayText;

    /// <summary>The current visual page: the slice of <see cref="DisplayText"/> that fills the window at
    /// the current size and font. This is what the reading pane renders, not the whole flow.</summary>
    [ObservableProperty]
    private string? _visiblePageText;

    /// <summary>Internal hyperlinks on the current visual page, slice-relative so the view can locate them
    /// directly in <see cref="VisiblePageText"/>. Original view only (empty while showing a translation).</summary>
    [ObservableProperty]
    private IReadOnlyList<VisibleLink> _visibleLinks = [];

    /// <summary>Whether <see cref="GoBackFromLinkAsync"/> has anywhere to return to.</summary>
    [ObservableProperty]
    private bool _canGoBackFromLink;

    /// <summary>An internal hyperlink located within the current visual page, in page-relative coordinates.</summary>
    public readonly record struct VisibleLink(int Start, int Length, string TargetKey);

    /// <summary>Right-to-left when showing a translation into an RTL language (Arabic, Hebrew, ...).</summary>
    public Avalonia.Media.FlowDirection ReadingFlowDirection =>
        ShowingTranslation && _target.IsRightToLeft
            ? Avalonia.Media.FlowDirection.RightToLeft
            : Avalonia.Media.FlowDirection.LeftToRight;

    /// <summary>Line height that keeps the reading pane legible as the font zooms.</summary>
    public double ReadingLineHeight => Math.Round(ReadingFontSize * 1.45);

    /// <summary>Label for the toggle control.</summary>
    public string ToggleLabel => ShowingTranslation ? "Show original" : "Show translation";

    public bool IsContentVisible => State == ReaderState.Content || IsTranslatingFallbackVisible;

    public bool IsStatusVisible =>
        State == ReaderState.Loading ? !IsTranslatingFallbackVisible :
        State is ReaderState.NoText or ReaderState.Error or ReaderState.NoDocument;

    public bool ShowRetry => State == ReaderState.Error;

    public bool IsTranslatingFallbackVisible =>
        State == ReaderState.Loading
        && ShowingTranslation
        && !string.IsNullOrWhiteSpace(OriginalText)
        && string.IsNullOrWhiteSpace(TranslationText);

    /// <summary>Translation of the current page failed: the reader keeps reading the original text while a
    /// small notice offers a retry, rather than being blocked by a full error screen.</summary>
    public bool IsTranslationFailedVisible =>
        TranslationFailed && ShowingTranslation && State == ReaderState.Content;

    public string CurrentPageLabel => $"Page {PageNumber}/{PageCount}";

    /// <summary>Percentage plus the raw counts: a segment is worth a fraction of a percent, so the number
    /// alone looks frozen even while the model is working steadily.</summary>
    public string TranslationPercentLabel
    {
        get
        {
            var percent = TranslationProgressPercent <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(TranslationProgressPercent), 1, 100);
            return TotalSegments <= 0
                ? "0% translated"
                : $"{percent}% translated ({TranslatedSegments}/{TotalSegments})";
        }
    }

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
            BuildContinuousText();
            // The Core page is unchanged but the language is not, so force a re-translation of it rather than
            // letting the "same page" fast-path skip the work.
            _currentIndex = -1;
            await TranslateVisiblePageAsync(ReadingDirection.Forward).ConfigureAwait(true);
        }
    }

    /// <summary>Override the detected source language for the current document, persist it, and
    /// re-translate (US3, FR-006).</summary>
    public async Task SetSourceOverrideAsync(LanguageCode? source)
    {
        _sourceOverride = source is { IsUnknown: false } ? source : null;

        if (_document is not null)
        {
            await UpdatePreferencesAsync(prefs => LanguageResolver.SetOverride(prefs, _document.Id, _sourceOverride))
                .ConfigureAwait(true);

            _prefetch.CancelPending();
            BuildContinuousText();
            _currentIndex = -1; // force a re-translation of the current Core page under the new source language
            await TranslateVisiblePageAsync(ReadingDirection.Forward).ConfigureAwait(true);
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
        BackgroundTranslation = prefs.BackgroundTranslation;
        _prefetch.Mode = prefs.BackgroundTranslation;
        if (!string.IsNullOrWhiteSpace(prefs.LastOpenedDocumentPath) && File.Exists(prefs.LastOpenedDocumentPath))
        {
            await OpenInternalAsync(prefs.LastOpenedDocumentPath).ConfigureAwait(true);
        }
    }

    /// <summary>Set the background-translation mode from a string (the reader's AA popover buttons).</summary>
    [RelayCommand]
    private Task SetBackgroundMode(string mode) =>
        Enum.TryParse<BackgroundTranslation>(mode, out var parsed)
            ? SetBackgroundTranslationAsync(parsed)
            : Task.CompletedTask;

    /// <summary>Apply and persist the background-translation mode. Off stops the whole-book work already in
    /// flight but still keeps the next couple of pages ready.</summary>
    public async Task SetBackgroundTranslationAsync(BackgroundTranslation mode)
    {
        BackgroundTranslation = mode;
        _prefetch.Mode = mode;
        await UpdatePreferencesAsync(prefs => prefs.BackgroundTranslation = mode).ConfigureAwait(true);

        // Re-schedule under the new mode without waiting for the next page turn — every mode, Off included,
        // does at least a short read-ahead.
        if (_document is not null)
        {
            SchedulePrefetch(ReadingDirection.Forward);
        }
    }

    /// <summary>Startup failed outright — say so instead of leaving the reader on "Opening…" forever.</summary>
    public void ShowStartupFailure(string message)
    {
        State = ReaderState.Error;
        StatusMessage = string.IsNullOrWhiteSpace(message) ? "Could not start the reader." : message;
    }

    [RelayCommand]
    public Task OpenAsync(string path) => OpenInternalAsync(path);

    private async Task OpenInternalAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _prefetch.CancelPending();
        (_reader as IDisposable)?.Dispose();
        UpdateTranslationProgress();
        OriginalText = null;
        TranslationText = null;
        State = ReaderState.Loading;
        StatusMessage = "Opening…";

        try
        {
            _reader = _readers.ResolveFor(path);
            _document = await _reader.OpenAsync(path, CancellationToken.None).ConfigureAwait(true);
            Title = _document.Title;
            PageCount = _document.PageCount;
            _links = _document.Links;
            _anchors = _document.Anchors;
            _linkReturnStack.Clear();
            CanGoBackFromLink = false;
            await _store.OpenAsync(_document.Id).ConfigureAwait(true); // everything this book has ever had translated
            var prefs = await UpdatePreferencesAsync(p => p.LastOpenedDocumentPath = path).ConfigureAwait(true);
            _sourceOverride = LanguageResolver.GetOverride(prefs, _document.Id);
            await MigrateLegacyPageTranslationsAsync(_document, prefs).ConfigureAwait(true);
            UpdateTranslationProgress();
            // Build the continuous reading flow (and the page anchors it scrolls to) before showing a page,
            // so the pane has text to render and GoToPageAsync can resolve the start page's char offset.
            await BuildReadingModelAsync(CancellationToken.None).ConfigureAwait(true);
            // Resume where this book was last left off, however it was opened (FR: per-book reading position).
            var startIndex = prefs.LastReadPageByDocument.TryGetValue(_document.Id, out var savedIndex)
                ? Math.Clamp(savedIndex, 0, _document.PageCount - 1)
                : 0;

            // Start reading at the visual page that opens this Core page, then translate/prefetch around it.
            _pageStartOffset = PageStartCharOffset(startIndex);
            _visitedPageStarts.Clear();
            ReSlice();
            RecountVisualPages();
            UpdateNavigation();
            await TranslateVisiblePageAsync(ReadingDirection.Forward).ConfigureAwait(true);
        }
        catch (DocumentOpenException ex)
        {
            _document = null;
            State = ReaderState.Error;
            StatusMessage = ex.Message;
            _logger.LogWarning(ex, "Failed to open document.");
        }
    }

    // AllowConcurrentExecutions: without it the generated async command reports CanExecute = false for as
    // long as a page is translating, which greys the toolbar button out mid-translation. Turning a page
    // already cancels the in-flight one (_pageCts), so overlapping calls are safe.
    //
    // Turning a page now advances the *visual* page — the viewport-sized slice of the continuous flow — so a
    // paragraph runs on across the break and every screen fills top to bottom. The Core page (translation
    // batch / prefetch anchor) is derived from wherever the visual page starts.
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task NextPageAsync()
    {
        var text = DisplayText;
        if (string.IsNullOrEmpty(text) || _metrics is not { } metrics)
        {
            return;
        }

        var consumed = _paginator.MeasurePage(text, _pageStartOffset, metrics);
        var next = _pageStartOffset + consumed;
        if (consumed <= 0 || next >= text.Length)
        {
            return; // already on the last visual page
        }

        _visitedPageStarts.Push(_pageStartOffset);
        _pageStartOffset = next;
        await OnVisualPageChangedAsync(ReadingDirection.Forward).ConfigureAwait(true);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task PreviousPageAsync()
    {
        if (_pageStartOffset <= 0 || _metrics is null)
        {
            return;
        }

        // O(1) when we have history for this page; otherwise re-walk from the start to find the previous one.
        _pageStartOffset = _visitedPageStarts.Count > 0
            ? _visitedPageStarts.Pop()
            : PreviousStartByRewalk();
        await OnVisualPageChangedAsync(ReadingDirection.Backward).ConfigureAwait(true);
    }

    /// <summary>The start offset of the visual page immediately before the current one, found by walking
    /// pages from the document start (used when the back-stack is empty, e.g. after a resize or jump).</summary>
    private int PreviousStartByRewalk()
    {
        if (DisplayText is not { } text || _metrics is not { } metrics || _pageStartOffset <= 0)
        {
            return 0;
        }

        var start = 0;
        while (true)
        {
            var consumed = _paginator.MeasurePage(text, start, metrics);
            if (consumed <= 0 || start + consumed >= _pageStartOffset)
            {
                return start;
            }

            start += consumed;
        }
    }

    /// <summary>Jump to a 1-based visual page number.</summary>
    public async Task JumpToPageAsync(int oneBasedPageNumber)
    {
        if (DisplayText is not { } text || _metrics is not { } metrics || text.Length == 0)
        {
            return;
        }

        var target = Math.Max(0, oneBasedPageNumber - 1);
        var start = 0;
        for (var p = 0; p < target; p++)
        {
            var consumed = _paginator.MeasurePage(text, start, metrics);
            if (consumed <= 0 || start + consumed >= text.Length)
            {
                break;
            }

            start += consumed;
        }

        var direction = start >= _pageStartOffset ? ReadingDirection.Forward : ReadingDirection.Backward;
        _pageStartOffset = start;
        _visitedPageStarts.Clear();
        await OnVisualPageChangedAsync(direction).ConfigureAwait(true);
    }

    /// <summary>Follow an internal hyperlink to its anchor: pushes the current page start onto the
    /// browser-style link-back stack (separate from ordinary page navigation, which is discarded — a link
    /// jump is a discontinuity for it) and lands on the target's segment.</summary>
    public async Task FollowLinkAsync(string targetKey)
    {
        if (_document is null || !_anchors.TryGetValue(targetKey, out var target))
        {
            return;
        }

        var destination = ResolveLinkDestination(target);
        _linkReturnStack.Push(_pageStartOffset);
        CanGoBackFromLink = true;
        _visitedPageStarts.Clear();
        _pageStartOffset = destination;
        await OnVisualPageChangedAsync(ReadingDirection.Forward).ConfigureAwait(true);
    }

    /// <summary>Browser-style Back for link jumps: returns to the page start recorded before the most
    /// recent <see cref="FollowLinkAsync"/>.</summary>
    [RelayCommand]
    public async Task GoBackFromLinkAsync()
    {
        if (_linkReturnStack.Count == 0)
        {
            return;
        }

        _pageStartOffset = Math.Clamp(_linkReturnStack.Pop(), 0, Math.Max(0, (DisplayText?.Length ?? 1) - 1));
        CanGoBackFromLink = _linkReturnStack.Count > 0;
        _visitedPageStarts.Clear();
        await OnVisualPageChangedAsync(ReadingDirection.Backward).ConfigureAwait(true);
    }

    /// <summary>A link's <see cref="LinkTarget.Offset"/> is measured into the segment's ORIGINAL text; the
    /// current flow may hold a (usually longer) translation instead, so the offset is clamped to the
    /// segment's current span before it is added to that segment's flow start — otherwise it could overshoot
    /// into the next segment.</summary>
    private int ResolveLinkDestination(LinkTarget target)
    {
        if (_segmentCharOffsets.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp(target.SegmentIndex, 0, _segmentCharOffsets.Length - 1);
        var start = _segmentCharOffsets[index];
        var end = index + 1 < _segmentCharOffsets.Length ? _segmentCharOffsets[index + 1] : (DisplayText?.Length ?? start);
        var span = Math.Max(0, end - start);
        var offsetWithinSegment = span == 0 ? 0 : Math.Clamp(target.Offset, 0, span - 1);
        return Math.Clamp(start + offsetWithinSegment, 0, Math.Max(0, (DisplayText?.Length ?? 1) - 1));
    }

    /// <summary>Recomputes <see cref="VisibleLinks"/> from the links whose flow range intersects the current
    /// visual page. Links are followable in the original view only.</summary>
    private void RebuildVisibleLinks()
    {
        if (ShowingTranslation || _metrics is null || _links.Count == 0
            || VisiblePageText is not { Length: > 0 } visible)
        {
            VisibleLinks = [];
            return;
        }

        var pageStart = _pageStartOffset;
        var pageEnd = pageStart + visible.Length;
        var result = new List<VisibleLink>();
        foreach (var link in _links)
        {
            if (link.SegmentIndex < 0 || link.SegmentIndex >= _segmentCharOffsets.Length)
            {
                continue; // stale reference from a differently-segmented document (shouldn't happen)
            }

            var flowStart = _segmentCharOffsets[link.SegmentIndex] + link.Start;
            var flowEnd = flowStart + link.Length;
            var overlapStart = Math.Max(flowStart, pageStart);
            var overlapEnd = Math.Min(flowEnd, pageEnd);
            if (overlapEnd <= overlapStart)
            {
                continue; // not on this visual page
            }

            result.Add(new VisibleLink(overlapStart - pageStart, overlapEnd - overlapStart, link.TargetKey));
        }

        VisibleLinks = result;
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

        // A different font size means different-sized pages; re-measure the current page in place so the
        // reader stays on roughly the same text (the back-stack's offsets were measured at the old font).
        if (_metrics is { } m)
        {
            _metrics = m with { FontSize = ReadingFontSize, LineHeight = ReadingLineHeight };
            _visitedPageStarts.Clear();
            ReSlice();
            RecountVisualPages();
            UpdateNavigation();
        }
    }

    [RelayCommand]
    public Task RetryAsync()
    {
        if (_document is null)
        {
            return Task.CompletedTask;
        }

        // A failed page stays in Content state (showing the original), which the fast-path would treat as
        // "already on screen"; force past it so retry actually re-runs the translation.
        _currentIndex = -1;
        return TranslateVisiblePageAsync(ReadingDirection.Forward);
    }

    /// <summary>After the visual page moves: re-slice the text on screen, renumber, refresh nav, and
    /// translate/prefetch the Core page the new visual page lands on.</summary>
    private Task OnVisualPageChangedAsync(ReadingDirection direction)
    {
        ReSlice();
        RecountVisualPages();
        UpdateNavigation();
        return TranslateVisiblePageAsync(direction);
    }

    /// <summary>Current visual page number and total, for the reader's position label. Cheap-ish but walks
    /// the whole flow, so it runs on navigation/metrics/font changes — not on every background segment.</summary>
    private void RecountVisualPages()
    {
        if (DisplayText is not { } text || _metrics is not { } metrics || text.Length == 0)
        {
            PageCount = 0;
            PageNumber = 0;
            return;
        }

        PageCount = _paginator.CountPages(text, metrics);
        var (index, _) = _paginator.PageContaining(text, _pageStartOffset, metrics);
        PageNumber = index + 1;
    }

    /// <summary>Map the current visual page to the Core page it starts in, make that the active page, and
    /// translate/prefetch it — so on-demand translation and the Off cap follow the reader's real position.</summary>
    private async Task TranslateVisiblePageAsync(ReadingDirection direction)
    {
        if (_document is null || _reader is null)
        {
            return;
        }

        var coreIndex = CorePageForOffset(_pageStartOffset);

        // Paging within a Core page that is already on screen needs no re-translation or re-prefetch.
        if (coreIndex == _currentIndex && State == ReaderState.Content)
        {
            await PersistLastReadPageAsync(coreIndex).ConfigureAwait(true);
            return;
        }

        _currentIndex = coreIndex;
        TranslationFailed = false; // a fresh page attempt clears any prior page's failure notice

        _pageCts?.Cancel();
        _pageCts?.Dispose();
        _pageCts = new CancellationTokenSource();
        var token = _pageCts.Token;

        try
        {
            var page = await _reader.GetPageAsync(_document, coreIndex, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            OriginalText = page.ExtractableText;
            TranslationText = null; // the previous page's translation must not linger over this one

            if (!page.HasText)
            {
                State = ReaderState.NoText;
                StatusMessage = "This page has no text to translate.";
            }
            else
            {
                // Enters the translating state only if the page is not already translated, so turning to
                // a cached page shows its text with no flash of "Translating…".
                await TranslateCurrentAsync(page, token).ConfigureAwait(true);
            }

            // After the page is on screen: neither the reader nor the view waits on this file write.
            await PersistLastReadPageAsync(coreIndex).ConfigureAwait(true);
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
                _logger.LogWarning(ex, "Failed to load page {Index}.", coreIndex);
            }
        }
    }

    /// <summary>The Core page index whose segments contain a char offset in the flow.</summary>
    private int CorePageForOffset(int charOffset) => PageForSegment(SegmentIndexAtOffset(charOffset));

    private async Task TranslateCurrentAsync(Page page, CancellationToken token)
    {
        var document = _document!;

        // The store already has every paragraph of this page → nothing to wait for; the service will
        // assemble it from what is on disk without touching the model.
        if (!IsFullyTranslated(page))
        {
            State = ReaderState.Loading;
            StatusMessage = "Translating…";
        }

        var result = await _translation
            .TranslateAsync(document, page, _target, _sourceOverride, _model, TranslationOrigin.OnDemand, token)
            .ConfigureAwait(true);

        if (token.IsCancellationRequested || result.PageIndex != _currentIndex)
        {
            return; // stale — belongs to a page we've navigated away from (FR-010)
        }

        ApplyTranslation(result, page.Index, token);
    }

    private bool IsFullyTranslated(Page page) =>
        page.Segments.Count > 0
        && page.Segments.All(s => _store.Contains(TranslationKey.For(s, ResolvedSource, _target, _model.ModelId)));

    /// <summary>The source language a translation is actually made from — the override if set, else what
    /// the document declares. Part of the key, so overriding it re-translates rather than reusing.</summary>
    private LanguageCode ResolvedSource => _sourceOverride ?? _document?.DetectedSourceLanguage ?? LanguageCode.Unknown;

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
            // Fold the freshly translated paragraphs into the continuous flow so the pane shows them.
            BuildContinuousText();
        }
        else
        {
            // A failed translation must not blank the page or block reading: keep the reader in the original
            // text (the continuous flow already renders it for this still-untranslated page) and raise a
            // small non-blocking notice that offers a retry.
            TranslationText = null;
            TranslationFailed = true;
            State = ReaderState.Content;
            StatusMessage = null;
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
        var text = DisplayText;
        CanGoPrevious = _metrics is not null && _pageStartOffset > 0;
        CanGoNext = _metrics is { } m && !string.IsNullOrEmpty(text)
            && _pageStartOffset + _paginator.MeasurePage(text!, _pageStartOffset, m) < text!.Length;
    }

    /// <summary>Sets up the continuous reading flow for a freshly opened (or repaginated) document: the
    /// ordered paragraphs and the first paragraph of each Core page, then the flow text itself.</summary>
    private async Task BuildReadingModelAsync(CancellationToken ct)
    {
        if (_document is null || _reader is null)
        {
            _orderedSegments = [];
            _pageFirstSegment = [];
            _segmentCharOffsets = [];
            DisplayText = null;
            return;
        }

        _orderedSegments = _document.Segments;

        // Walk the pages in order to learn which paragraph each one begins at — the anchor a page turn
        // scrolls to. (The union of every page's segments is exactly the document's ordered segments.)
        var firsts = new int[_document.PageCount];
        var running = 0;
        for (var p = 0; p < _document.PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            firsts[p] = Math.Clamp(running, 0, Math.Max(0, _orderedSegments.Count - 1));
            running += (await _reader.GetPageAsync(_document, p, ct).ConfigureAwait(true)).Segments.Count;
        }

        _pageFirstSegment = firsts;
        BuildContinuousText();
    }

    /// <summary>The view reports the space the text actually gets (column width, viewport height, font),
    /// on open and whenever the window is resized or the font zoomed. Re-slices the current page.</summary>
    public void SetReadingMetrics(double columnWidth, double viewportHeight, Avalonia.Media.Typeface typeface)
    {
        if (columnWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        _metrics = new ReadingPageMetrics(
            columnWidth, viewportHeight, ReadingFontSize, ReadingLineHeight, typeface, ReadingFlowDirection);

        // Offsets on the back-stack were measured at the old size; a resize invalidates them.
        _visitedPageStarts.Clear();
        ReSlice();
        RecountVisualPages();
        UpdateNavigation();
    }

    /// <summary>Recompute <see cref="VisiblePageText"/> from the flow, the current page start, and metrics.</summary>
    private void ReSlice()
    {
        var text = DisplayText;
        if (string.IsNullOrEmpty(text) || _metrics is not { } metrics)
        {
            VisiblePageText = text; // no metrics yet: show the flow so nothing is blank pre-layout
            RebuildVisibleLinks();
            return;
        }

        _pageStartOffset = Math.Clamp(_pageStartOffset, 0, Math.Max(0, text.Length - 1));
        var consumed = _paginator.MeasurePage(text, _pageStartOffset, metrics);
        VisiblePageText = consumed <= 0 ? string.Empty : text.Substring(_pageStartOffset, consumed);
        RebuildVisibleLinks();
    }

    /// <summary>Rebuilds the flow text from the store — each paragraph translated where held, original
    /// otherwise — and records where each paragraph starts so the view can scroll to a page's anchor.</summary>
    private void BuildContinuousText()
    {
        if (_orderedSegments.Count == 0)
        {
            _segmentCharOffsets = [];
            DisplayText = null;
            ReSlice();
            return;
        }

        // Snapshot the current flow so the reading position survives the rebuild: the same char offset lands
        // in a different passage once paragraphs change length (translation⇄original, or a landing
        // translation), so anchor every offset to its paragraph and how far into it, then remap below.
        var oldOffsets = _segmentCharOffsets;
        var oldFlowLength = DisplayText?.Length ?? 0;

        var source = ResolvedSource;
        var offsets = new int[_orderedSegments.Count];
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < _orderedSegments.Count; i++)
        {
            offsets[i] = builder.Length;
            var paragraph = _orderedSegments[i];
            if (ShowingTranslation
                && _store.TryGet(TranslationKey.For(paragraph, source, _target, _model.ModelId), out var translated)
                && !string.IsNullOrWhiteSpace(translated))
            {
                builder.Append(translated);
            }
            else
            {
                builder.Append(paragraph);
            }

            if (i < _orderedSegments.Count - 1)
            {
                builder.Append("\n\n");
            }
        }

        var newFlowLength = builder.Length;
        _segmentCharOffsets = offsets;
        DisplayText = builder.ToString();

        // Carry the page start (and the back-stack) from the old flow to the new one, paragraph by
        // paragraph, so toggling original⇄translation or a landing translation keeps the reader on the same
        // passage rather than jumping. (The paragraph count is fixed for a document, so old and new offsets
        // line up index-for-index.)
        if (oldOffsets.Length == offsets.Length && oldOffsets.Length > 0)
        {
            _pageStartOffset = RemapOffset(_pageStartOffset, oldOffsets, oldFlowLength, offsets, newFlowLength);
            if (_visitedPageStarts.Count > 0)
            {
                var remapped = _visitedPageStarts
                    .Select(o => RemapOffset(o, oldOffsets, oldFlowLength, offsets, newFlowLength))
                    .ToArray();
                _visitedPageStarts.Clear();
                for (var i = remapped.Length - 1; i >= 0; i--) // rebuild top-of-stack last to keep order
                {
                    _visitedPageStarts.Push(remapped[i]);
                }
            }

            if (_linkReturnStack.Count > 0)
            {
                var remappedLinkReturns = _linkReturnStack
                    .Select(o => RemapOffset(o, oldOffsets, oldFlowLength, offsets, newFlowLength))
                    .ToArray();
                _linkReturnStack.Clear();
                for (var i = remappedLinkReturns.Length - 1; i >= 0; i--) // rebuild top-of-stack last to keep order
                {
                    _linkReturnStack.Push(remappedLinkReturns[i]);
                }
            }
        }

        ReSlice();
    }

    /// <summary>Maps a char offset from one flow to another by anchoring it to its paragraph and the fraction
    /// of the way through it, so a page start stays on the same passage when paragraphs change length.</summary>
    private static int RemapOffset(int offset, int[] oldOffsets, int oldFlowLength, int[] newOffsets, int newFlowLength)
    {
        var segment = SegmentIndexIn(oldOffsets, offset);
        var oldStart = oldOffsets[segment];
        var oldSpan = (segment + 1 < oldOffsets.Length ? oldOffsets[segment + 1] : oldFlowLength) - oldStart;
        var newStart = newOffsets[segment];
        var newSpan = (segment + 1 < newOffsets.Length ? newOffsets[segment + 1] : newFlowLength) - newStart;

        var into = offset - oldStart;
        var mapped = oldSpan > 0 ? (int)Math.Round((double)into / oldSpan * newSpan) : 0;
        return newStart + Math.Clamp(mapped, 0, Math.Max(0, newSpan));
    }

    /// <summary>The index of the paragraph whose span contains <paramref name="charOffset"/>, in the given
    /// paragraph-start table (half-open intervals).</summary>
    private static int SegmentIndexIn(int[] offsets, int charOffset)
    {
        if (offsets.Length == 0)
        {
            return 0;
        }

        var i = Array.BinarySearch(offsets, charOffset);
        return i >= 0 ? i : Math.Clamp(~i - 1, 0, offsets.Length - 1);
    }

    /// <summary>Char offset in <see cref="DisplayText"/> where a Core page begins.</summary>
    private int PageStartCharOffset(int pageIndex)
    {
        if (_pageFirstSegment.Length == 0 || _segmentCharOffsets.Length == 0)
        {
            return 0;
        }

        var segment = _pageFirstSegment[Math.Clamp(pageIndex, 0, _pageFirstSegment.Length - 1)];
        return _segmentCharOffsets[Math.Clamp(segment, 0, _segmentCharOffsets.Length - 1)];
    }

    private int SegmentIndexAtOffset(int charOffset) => SegmentIndexIn(_segmentCharOffsets, charOffset);

    private int PageForSegment(int segmentIndex)
    {
        // The last page whose first segment is <= segmentIndex.
        var page = 0;
        for (var p = 0; p < _pageFirstSegment.Length; p++)
        {
            if (_pageFirstSegment[p] <= segmentIndex)
            {
                page = p;
            }
            else
            {
                break;
            }
        }

        return page;
    }

    partial void OnShowingTranslationChanged(bool value)
    {
        // Original and translation paginate differently (translated paragraphs are usually longer), so after
        // remapping the reader onto the same passage, refresh the visual page count and nav for the new flow.
        BuildContinuousText();
        RecountVisualPages();
        UpdateNavigation();
        RebuildVisibleLinks();
    }

    /// <summary>
    /// A segment was translated — possibly by the background prefetch, on its own thread. Refresh
    /// progress on the UI thread; the store itself has already persisted the segment.
    /// </summary>
    private void OnSegmentStored(object? sender, EventArgs e) => RunOnUiThread(() =>
    {
        UpdateTranslationProgress();
        // A background/prefetch translation landing changes what the flow shows only while translations
        // are on screen; in original mode the pane is unaffected, so skip the rebuild.
        if (ShowingTranslation)
        {
            BuildContinuousText();
        }
    });

    /// <summary>Runs view-facing updates on the thread the view-model was created on (the UI thread in the
    /// app; inline in tests, which have no synchronization context).</summary>
    private void RunOnUiThread(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    /// <summary>
    /// Progress is measured in segments, not pages: segments are what actually get translated, and unlike
    /// pages they do not move when the book is repaginated — so the percentage never jumps around.
    /// </summary>
    private void UpdateTranslationProgress()
    {
        if (_document is null || _document.Segments.Count == 0)
        {
            TranslatedSegments = 0;
            TotalSegments = 0;
            TranslationProgressPercent = 0;
            return;
        }

        var total = _document.Segments.Count;
        TotalSegments = total;
        var done = _store.CountStored(_document.Segments.Select(s => TranslationKey.For(s, ResolvedSource, _target, _model.ModelId)));
        TranslatedSegments = Math.Clamp(done, 0, total);
        TranslationProgressPercent = (double)TranslatedSegments * 100d / total;
    }

    /// <summary>
    /// One-time rescue of translations made before segments existed. They were stored per page, and a page
    /// translation is the paragraph-by-paragraph translation of that page's text — so where the paragraph
    /// counts line up, each pair can be recovered as a segment. Pages whose counts do not line up are
    /// dropped rather than guessed at.
    /// </summary>
    private async Task MigrateLegacyPageTranslationsAsync(Document document, ReaderPreferences preferences)
    {
        if (preferences.MigratedDocuments.Contains(document.Id, StringComparer.OrdinalIgnoreCase)
            || !preferences.TranslationCacheByDocument.TryGetValue(document.Id, out var entries)
            || entries.Count == 0)
        {
            return;
        }

        var sourceByHash = await CollectPageTextsAcrossLayoutsAsync(document).ConfigureAwait(true);
        var recovered = new Dictionary<TranslationKey, string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.TextHash)
                || string.IsNullOrWhiteSpace(entry.ModelId)
                || string.IsNullOrWhiteSpace(entry.Text)
                || !sourceByHash.TryGetValue(entry.TextHash, out var originalText))
            {
                continue; // made from text this book no longer contains
            }

            var target = new LanguageCode(entry.TargetLanguage);
            var sourceSegments = Page.SplitIntoSegments(originalText);
            var translatedSegments = Page.SplitIntoSegments(entry.Text);
            if (target.IsUnknown || sourceSegments.Count == 0 || sourceSegments.Count != translatedSegments.Count)
            {
                continue; // the model merged or split paragraphs — cannot align them safely
            }

            var entrySource = string.IsNullOrWhiteSpace(entry.SourceLanguage) ? LanguageCode.Unknown : new LanguageCode(entry.SourceLanguage);
            for (var i = 0; i < sourceSegments.Count; i++)
            {
                recovered[TranslationKey.For(sourceSegments[i], entrySource, target, entry.ModelId)] = translatedSegments[i];
            }
        }

        await _store.ImportAsync(recovered).ConfigureAwait(true);

        // Record that this book has been migrated, but keep the legacy entries: they are the only copy of
        // that work, and a better alignment may yet be able to rescue more of it.
        await UpdatePreferencesAsync(prefs => prefs.MigratedDocuments.Add(document.Id)).ConfigureAwait(true);
        _logger.LogInformation(
            "Recovered {Segments} segments from {Pages} page translations stored by an earlier version.",
            recovered.Count,
            entries.Count);
    }

    /// <summary>
    /// Page texts a legacy translation could have been made from. Legacy translations are keyed by the hash
    /// of a whole page, and page boundaries move with the reading surface, so the pages as laid out right
    /// now are usually not the pages those translations were made against. Re-paginating the book across a
    /// range of widths recovers the layouts they belong to.
    /// </summary>
    private async Task<Dictionary<string, string>> CollectPageTextsAcrossLayoutsAsync(Document document)
    {
        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        async Task CollectAsync(Document layout)
        {
            for (var i = 0; i < layout.PageCount; i++)
            {
                var page = await _reader!.GetPageAsync(layout, i, CancellationToken.None).ConfigureAwait(true);
                byHash[TranslationKey.HashText(page.ExtractableText)] = page.ExtractableText;
            }
        }

        await CollectAsync(document).ConfigureAwait(true);

        if (_reader is not IReflowableDocumentReader reflowable)
        {
            return byHash; // a PDF's pages never move
        }

        for (var width = 600d; width <= 2400d; width += 60d)
        {
            if (!reflowable.UpdateViewport(width, 800d))
            {
                continue;
            }

            var layout = await _reader.OpenAsync(document.SourcePath, CancellationToken.None).ConfigureAwait(true);
            await CollectAsync(layout).ConfigureAwait(true);
        }

        // Put the reader back on a sensible default layout; the visual paginator drives display now, and the
        // continuous flow is rebuilt from the document's segments regardless of Core page size.
        reflowable.UpdateViewport(BaselineLayoutWidth, BaselineLayoutHeight);
        _document = await _reader.OpenAsync(document.SourcePath, CancellationToken.None).ConfigureAwait(true);
        return byHash;
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
