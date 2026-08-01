# Page-by-page Reading Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the reader's free-scrolling continuous flow with viewport-fitted visual pages that split mid-paragraph, so each screen shows exactly what fits and turning advances one page — with no scrollbar.

**Architecture:** The whole-document flow (`ReaderViewModel.DisplayText`) is unchanged and stays the source of truth. A new `ReadingPaginator` measures, with Avalonia `TextLayout`, how much of that flow fits one page at the current width/height/font, yielding **visual pages** used only for display and navigation. The existing Core page/segment model (translation batching, prefetch, Off cap, progress %, store keys) is untouched; the "current Core page" that drives prefetch is derived from the visual page's start offset. The reading view swaps its `ScrollViewer` for a clipped `Panel`.

**Tech Stack:** C# / .NET 10, Avalonia UI (headless for tests), CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), xUnit (`[AvaloniaFact]` for view/layout tests).

## Global Constraints

- Target framework: `net10.0`. Warnings are errors — no unused fields/usings (a leftover unused field previously broke the build).
- `DisplayText` MUST remain the whole-document continuous flow (every segment, translated where the store holds it, original otherwise). Do not repurpose it to a page slice.
- Segments and their order are pagination-independent (`_orderedSegments`); therefore `DisplayText`'s content and length do not change when Core pages are repaginated. Visual offsets into `DisplayText` stay valid across Core reflow.
- Translation, prefetch, Off/Gentle/FullSpeed, progress %, and store keys stay on the Core page/segment model. Do not change `TranslationService`, `PrefetchCoordinator`, or the store.
- Reading inset/column constants live in `ReaderView.axaml.cs` (`ReadingInsetX=24`, `ReadingInsetTop=72`, `ReadingInsetBottom=56`, `ReadingColumnMaxWidth=720`) and must match the `PageText` margin/`MaxWidth` in `ReaderView.axaml`.
- `ReadingLineHeight => Math.Round(ReadingFontSize * 1.45)`; measurement must use the same line height the text block renders with.
- Run `dotnet build` (must be 0 warnings / 0 errors) and the relevant `dotnet test` project after each task.

---

## File Structure

- **Create** `src/BabelRead.App/Reading/ReadingPaginator.cs` — `ReadingPageMetrics` record + `ReadingPaginator` (pure measurement over a string using `TextLayout`). One responsibility: given text + a start offset + metrics, say how many characters fit one page, and find the page containing an offset.
- **Create** `tests/BabelRead.App.Tests/Reading/ReadingPaginatorTests.cs` — headless `[AvaloniaFact]` tests for the paginator.
- **Modify** `src/BabelRead.App/ViewModels/ReaderViewModel.cs` — add `VisiblePageText`, `_pageStartOffset`, visual page count, metrics input, visual-page navigation, and derive the Core page from the visual offset.
- **Modify** `tests/BabelRead.App.Tests/ReaderViewModelTests.cs` — update page-count/label assertions to visual pages; add visual-navigation tests.
- **Modify** `src/BabelRead.App/Views/ReaderView.axaml` — `ScrollViewer` → clipped `Panel`, bind `VisiblePageText`.
- **Modify** `src/BabelRead.App/Views/ReaderView.axaml.cs` — push `ReadingPageMetrics` to the VM on resize/font change; keyboard turns move visual pages; drop scroll-offset reset.
- **Modify** `tests/BabelRead.App.Tests/ViewSmokeTests.cs` — replace the scroll-based centering test with a page-fit smoke test.

---

## Task 1: `ReadingPaginator.MeasurePage` — how much fits one page

**Files:**
- Create: `src/BabelRead.App/Reading/ReadingPaginator.cs`
- Test: `tests/BabelRead.App.Tests/Reading/ReadingPaginatorTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct ReadingPageMetrics(double ColumnWidth, double ViewportHeight, double FontSize, double LineHeight, Avalonia.Media.Typeface Typeface, Avalonia.Media.FlowDirection FlowDirection)`
  - `sealed class ReadingPaginator { int MeasurePage(string text, int start, ReadingPageMetrics metrics); }`
  - `MeasurePage` returns the number of characters, starting at `start`, that fill one page — always at least the first line's length when any text remains, so it can never return 0 for non-empty remaining text (guarantees forward progress).

- [ ] **Step 1: Write the failing test**

Create `tests/BabelRead.App.Tests/Reading/ReadingPaginatorTests.cs`:

```csharp
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using BabelRead.App.Reading;
using Xunit;

namespace BabelRead.App.Tests.Reading;

public class ReadingPaginatorTests
{
    private static ReadingPageMetrics Metrics(double width = 400, double height = 200, double font = 16) =>
        new(width, height, font, LineHeight: 24, Typeface.Default, FlowDirection.LeftToRight);

    // A long body of text, many sentences, so it wraps to far more than one page.
    private static string LongText()
    {
        var paragraph = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 12));
        return string.Join("\n\n", Enumerable.Repeat(paragraph, 20));
    }

    [AvaloniaFact]
    public void A_page_consumes_some_text_but_not_all_of_a_long_document()
    {
        var paginator = new ReadingPaginator();
        var text = LongText();

        var consumed = paginator.MeasurePage(text, 0, Metrics());

        Assert.True(consumed > 0, "a page must consume some text");
        Assert.True(consumed < text.Length, "a long document must not fit on one page");
    }

    [AvaloniaFact]
    public void Successive_pages_cover_the_whole_document_with_no_loss_or_overlap()
    {
        var paginator = new ReadingPaginator();
        var text = LongText();
        var metrics = Metrics();

        var start = 0;
        var pages = 0;
        while (start < text.Length)
        {
            var consumed = paginator.MeasurePage(text, start, metrics);
            Assert.True(consumed > 0, "every page must make forward progress");
            start += consumed;
            pages++;
            Assert.True(pages < 10_000, "pagination must terminate");
        }

        Assert.Equal(text.Length, start); // exact cover: no loss, no overlap
        Assert.True(pages > 1);
    }

    [AvaloniaFact]
    public void Forward_progress_is_guaranteed_even_for_an_unbreakable_line_wider_than_the_column()
    {
        var paginator = new ReadingPaginator();
        var text = new string('X', 500); // a single token far wider than the column

        var consumed = paginator.MeasurePage(text, 0, Metrics(width: 100, height: 50));

        Assert.True(consumed > 0, "an over-long line must still advance");
    }

    [AvaloniaFact]
    public void Empty_or_exhausted_text_consumes_nothing()
    {
        var paginator = new ReadingPaginator();
        Assert.Equal(0, paginator.MeasurePage("", 0, Metrics()));
        Assert.Equal(0, paginator.MeasurePage("abc", 3, Metrics()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj --filter "FullyQualifiedName~ReadingPaginatorTests"`
Expected: FAIL to build — `ReadingPaginator` / `ReadingPageMetrics` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/BabelRead.App/Reading/ReadingPaginator.cs`:

```csharp
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace BabelRead.App.Reading;

/// <summary>The rendering parameters a page is measured against — must match what the reading
/// text block actually draws with, or measured pages will not line up with rendered ones.</summary>
public readonly record struct ReadingPageMetrics(
    double ColumnWidth,
    double ViewportHeight,
    double FontSize,
    double LineHeight,
    Typeface Typeface,
    FlowDirection FlowDirection);

/// <summary>Cuts the continuous reading flow into viewport-sized visual pages. A page is the run of
/// characters whose wrapped lines fill the viewport height; a paragraph therefore splits across the
/// page break and resumes at the top of the next page (printed-book style).</summary>
public sealed class ReadingPaginator
{
    /// <summary>Characters, starting at <paramref name="start"/>, that fill one page. Never 0 while
    /// text remains: at least the first line is consumed so pagination always advances.</summary>
    public int MeasurePage(string text, int start, ReadingPageMetrics metrics)
    {
        if (string.IsNullOrEmpty(text) || start < 0 || start >= text.Length)
        {
            return 0;
        }

        var maxLines = Math.Max(1, (int)Math.Floor(metrics.ViewportHeight / Math.Max(1, metrics.LineHeight)));
        var remaining = text[start..];

        var layout = new TextLayout(
            remaining,
            metrics.Typeface,
            metrics.FontSize,
            foreground: Brushes.Black,
            textAlignment: TextAlignment.Left,
            textWrapping: TextWrapping.Wrap,
            textTrimming: TextTrimming.None,
            flowDirection: metrics.FlowDirection,
            maxWidth: metrics.ColumnWidth,
            maxHeight: double.PositiveInfinity,
            maxLines: maxLines,
            lineHeight: metrics.LineHeight);

        var consumed = 0;
        foreach (var line in layout.TextLines)
        {
            consumed += line.Length;
        }

        // Guarantee progress: if measuring produced nothing (e.g. an unbreakable token), take one line.
        if (consumed <= 0)
        {
            consumed = layout.TextLines.Count > 0 ? Math.Max(1, layout.TextLines[0].Length) : 1;
        }

        return Math.Min(consumed, remaining.Length);
    }
}
```

Note: the `TextLayout` constructor overload above is illustrative of the parameters needed (typeface, font size, wrap, `maxWidth`, `maxLines`, `lineHeight`). If the installed Avalonia version's constructor signature differs, use the available overload/`TextLayout` property setters that set the same values — the behavior contract (wrap at `ColumnWidth`, cap at `maxLines`, use `LineHeight`) is what matters.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj --filter "FullyQualifiedName~ReadingPaginatorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/BabelRead.App/Reading/ReadingPaginator.cs tests/BabelRead.App.Tests/Reading/ReadingPaginatorTests.cs
git commit -m "Add ReadingPaginator.MeasurePage for viewport-fitted pages"
```

---

## Task 2: `ReadingPaginator.PageContaining` — locate a page by offset

Used for resume-position and jump-to-page: map a char offset into `DisplayText` to the visual page that contains it, plus that page's start offset.

**Files:**
- Modify: `src/BabelRead.App/Reading/ReadingPaginator.cs`
- Test: `tests/BabelRead.App.Tests/Reading/ReadingPaginatorTests.cs`

**Interfaces:**
- Consumes: `MeasurePage` (Task 1).
- Produces: `(int PageIndex, int PageStart) PageContaining(string text, int charOffset, ReadingPageMetrics metrics)` — walks pages from 0 and returns the 0-based index and start offset of the page whose char range brackets `charOffset` (clamped to the last page for offsets past the end). Also `int CountPages(string text, ReadingPageMetrics metrics)`.

- [ ] **Step 1: Write the failing test**

Append to `ReadingPaginatorTests.cs`:

```csharp
    [AvaloniaFact]
    public void PageContaining_returns_the_page_whose_range_brackets_the_offset()
    {
        var paginator = new ReadingPaginator();
        var text = LongText();
        var metrics = Metrics();

        // Discover real page boundaries.
        var starts = new List<int> { 0 };
        var cursor = 0;
        while (cursor < text.Length)
        {
            cursor += paginator.MeasurePage(text, cursor, metrics);
            if (cursor < text.Length) starts.Add(cursor);
        }

        Assert.True(starts.Count >= 3, "need several pages for a meaningful test");

        // An offset just inside page 2 must resolve to page index 2 and page 2's start.
        var probe = starts[2] + 1;
        var (pageIndex, pageStart) = paginator.PageContaining(text, probe, metrics);
        Assert.Equal(2, pageIndex);
        Assert.Equal(starts[2], pageStart);

        Assert.Equal(starts.Count, paginator.CountPages(text, metrics));
    }

    [AvaloniaFact]
    public void PageContaining_clamps_offsets_past_the_end_to_the_last_page()
    {
        var paginator = new ReadingPaginator();
        var text = LongText();
        var metrics = Metrics();

        var last = paginator.CountPages(text, metrics) - 1;
        var (pageIndex, _) = paginator.PageContaining(text, text.Length + 999, metrics);
        Assert.Equal(last, pageIndex);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj --filter "FullyQualifiedName~ReadingPaginatorTests"`
Expected: FAIL to build — `PageContaining` / `CountPages` not defined.

- [ ] **Step 3: Write minimal implementation**

Add to `ReadingPaginator`:

```csharp
    /// <summary>The 0-based visual page index and start offset of the page containing
    /// <paramref name="charOffset"/> (clamped into range).</summary>
    public (int PageIndex, int PageStart) PageContaining(string text, int charOffset, ReadingPageMetrics metrics)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, 0);
        }

        var target = Math.Clamp(charOffset, 0, text.Length - 1);
        var start = 0;
        var index = 0;
        while (true)
        {
            var consumed = MeasurePage(text, start, metrics);
            if (consumed <= 0 || start + consumed > target || start + consumed >= text.Length)
            {
                return (index, start);
            }

            start += consumed;
            index++;
        }
    }

    /// <summary>Total number of visual pages in <paramref name="text"/> at these metrics.</summary>
    public int CountPages(string text, ReadingPageMetrics metrics)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var start = 0;
        var pages = 0;
        while (start < text.Length)
        {
            var consumed = MeasurePage(text, start, metrics);
            if (consumed <= 0)
            {
                break;
            }

            start += consumed;
            pages++;
        }

        return pages;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj --filter "FullyQualifiedName~ReadingPaginatorTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/BabelRead.App/Reading/ReadingPaginator.cs tests/BabelRead.App.Tests/Reading/ReadingPaginatorTests.cs
git commit -m "Add ReadingPaginator.PageContaining and CountPages"
```

---

## Task 3: VM — current page slice, metrics input, re-slice on flow change

Adds the visual-page state to the view-model and the property the view renders, without navigation yet. The current page is sliced from `DisplayText` at `_pageStartOffset`.

**Files:**
- Modify: `src/BabelRead.App/ViewModels/ReaderViewModel.cs`
- Test: `tests/BabelRead.App.Tests/ReaderViewModelTests.cs`

**Interfaces:**
- Consumes: `ReadingPaginator`, `ReadingPageMetrics` (Tasks 1-2); existing `DisplayText`, `BuildContinuousText`, `_orderedSegments`, `_segmentCharOffsets`.
- Produces:
  - `string? VisiblePageText { get; }` — the current visual page's slice of `DisplayText` (bound by the view).
  - `void SetReadingMetrics(double columnWidth, double viewportHeight, Typeface typeface)` — the view calls this on resize/font change; stores metrics and re-slices.
  - `int _pageStartOffset` field — char offset of the current page start.
  - A private `ReSlice()` that recomputes `VisiblePageText` from `DisplayText`, `_pageStartOffset`, and the current metrics.

- [ ] **Step 1: Write the failing test**

Add to `ReaderViewModelTests.cs`:

```csharp
    [Fact]
    public async Task Visible_page_text_is_a_prefix_of_the_whole_flow_once_metrics_are_set()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf(string.Join(" ", Enumerable.Repeat("word", 400))));

        vm.SetReadingMetrics(columnWidth: 400, viewportHeight: 200,
            typeface: Avalonia.Media.Typeface.Default);

        Assert.False(string.IsNullOrEmpty(vm.VisiblePageText));
        Assert.StartsWith(vm.VisiblePageText!, vm.DisplayText!, StringComparison.Ordinal);
        Assert.True(vm.VisiblePageText!.Length < vm.DisplayText!.Length, "a long doc must not fit one page");
    }
```

This test uses Avalonia types, so `ReaderViewModelTests` must run under a headless platform. If the class is not already `[AvaloniaFact]`-based, mark this one test `[AvaloniaFact]` (add `using Avalonia.Headless.XUnit;`) instead of `[Fact]`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj --filter "FullyQualifiedName~Visible_page_text_is_a_prefix"`
Expected: FAIL to build — `SetReadingMetrics` / `VisiblePageText` not defined.

- [ ] **Step 3: Write minimal implementation**

In `ReaderViewModel.cs`:

Add fields near the other flow state (after `_segmentCharOffsets`):

```csharp
    private readonly ReadingPaginator _paginator = new();
    private ReadingPageMetrics? _metrics;
    private int _pageStartOffset;
```

Add the bound property near `DisplayText`:

```csharp
    /// <summary>The current visual page: the slice of <see cref="DisplayText"/> that fills the window at
    /// the current size and font. This is what the reading pane renders, not the whole flow.</summary>
    [ObservableProperty]
    private string? _visiblePageText;
```

Add the metrics entry point and the re-slice helper (place near `BuildContinuousText`):

```csharp
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
        ReSlice();
    }

    /// <summary>Recompute <see cref="VisiblePageText"/> from the flow, the current page start, and metrics.</summary>
    private void ReSlice()
    {
        var text = DisplayText;
        if (string.IsNullOrEmpty(text) || _metrics is not { } metrics)
        {
            VisiblePageText = text; // no metrics yet: show the flow so nothing is blank pre-layout
            return;
        }

        _pageStartOffset = Math.Clamp(_pageStartOffset, 0, Math.Max(0, text.Length - 1));
        var consumed = _paginator.MeasurePage(text, _pageStartOffset, metrics);
        VisiblePageText = consumed <= 0 ? string.Empty : text.Substring(_pageStartOffset, consumed);
    }
```

Re-slice whenever the flow is rebuilt: at the end of `BuildContinuousText()` (after `DisplayText = builder.ToString();`) add:

```csharp
        ReSlice();
```

And in the empty-flow branch of `BuildContinuousText` (where `DisplayText = null;` is set) also add `ReSlice();` so `VisiblePageText` clears.

Add the `using`:

```csharp
using BabelRead.App.Reading;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj --filter "FullyQualifiedName~Visible_page_text_is_a_prefix"`
Expected: PASS.

- [ ] **Step 5: Run the full App test project to catch fallout**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj`
Expected: PASS (existing tests still green; they assert on `DisplayText`/`OriginalText`, which are unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/BabelRead.App/ViewModels/ReaderViewModel.cs tests/BabelRead.App.Tests/ReaderViewModelTests.cs
git commit -m "Add visual page slice and metrics input to ReaderViewModel"
```

---

## Task 4: VM — visual-page navigation and visual page numbering

Turns Prev/Next (and the `PageNumber`/`PageCount`/`CurrentPageLabel` shown to the reader) into **visual** pages. Keeps the existing command names so XAML/keyboard bindings do not change.

**Files:**
- Modify: `src/BabelRead.App/ViewModels/ReaderViewModel.cs`
- Test: `tests/BabelRead.App.Tests/ReaderViewModelTests.cs`

**Interfaces:**
- Consumes: `_paginator`, `_metrics`, `_pageStartOffset`, `ReSlice`, `DisplayText` (Task 3).
- Produces:
  - `NextPageAsync()` / `PreviousPageAsync()` now move one **visual** page (advance/retreat `_pageStartOffset`), re-slice, and refresh navigation + the derived Core page (Task 5 wires prefetch to it).
  - `CanGoNext` = there is text after the current page; `CanGoPrevious` = `_pageStartOffset > 0`.
  - `PageNumber` / `PageCount` reflect visual pages; `CurrentPageLabel` stays `"Page {PageNumber}/{PageCount}"`.
  - A `_visitedPageStarts` `Stack<int>` for O(1) back-navigation; cleared and rebuilt (via `PageContaining`) after a flow rebuild or metrics change.

- [ ] **Step 1: Write the failing test**

Add to `ReaderViewModelTests.cs` (headless — `[AvaloniaFact]`):

```csharp
    [AvaloniaFact]
    public async Task Next_and_previous_move_one_visual_page_and_round_trip()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf(string.Join(" ", Enumerable.Repeat("word", 800))));
        vm.SetReadingMetrics(400, 200, Avalonia.Media.Typeface.Default);

        Assert.True(vm.PageCount > 1);
        Assert.Equal(1, vm.PageNumber);
        Assert.False(vm.CanGoPrevious);

        var firstPage = vm.VisiblePageText;
        await vm.NextPageAsync();

        Assert.Equal(2, vm.PageNumber);
        Assert.True(vm.CanGoPrevious);
        Assert.NotEqual(firstPage, vm.VisiblePageText);

        await vm.PreviousPageAsync();
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(firstPage, vm.VisiblePageText); // back to exactly the first page
    }

    [AvaloniaFact]
    public async Task Cannot_advance_past_the_last_visual_page()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf("A short one-page document."));
        vm.SetReadingMetrics(400, 400, Avalonia.Media.Typeface.Default);

        Assert.Equal(1, vm.PageCount);
        Assert.False(vm.CanGoNext);
        await vm.NextPageAsync(); // no-op
        Assert.Equal(1, vm.PageNumber);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj --filter "FullyQualifiedName~visual_page"`
Expected: FAIL — Prev/Next still move Core pages; `PageCount` is the Core count.

- [ ] **Step 3: Write minimal implementation**

In `ReaderViewModel.cs`:

Add the back-stack field beside `_pageStartOffset`:

```csharp
    private readonly Stack<int> _visitedPageStarts = new();
    private int _visualPageCount;
```

Replace the bodies of the navigation commands. Find `NextPageAsync` / `PreviousPageAsync` (`[RelayCommand(AllowConcurrentExecutions = true)]`) and change them to move visual pages:

```csharp
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
            return; // already on the last page
        }

        _visitedPageStarts.Push(_pageStartOffset);
        _pageStartOffset = next;
        await OnVisualPageChangedAsync(ReadingDirection.Forward).ConfigureAwait(true);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task PreviousPageAsync()
    {
        if (_pageStartOffset <= 0)
        {
            return;
        }

        // O(1) when we have history; otherwise recompute the previous page from the document start.
        _pageStartOffset = _visitedPageStarts.Count > 0
            ? _visitedPageStarts.Pop()
            : PreviousStartByRewalk();
        await OnVisualPageChangedAsync(ReadingDirection.Backward).ConfigureAwait(true);
    }

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
```

Add the shared post-navigation step (re-slice, renumber, refresh nav, and let Task 5 hook the Core page):

```csharp
    private Task OnVisualPageChangedAsync(ReadingDirection direction)
    {
        ReSlice();
        RecountVisualPages();
        UpdateNavigation();
        return TranslateVisiblePageAsync(direction); // defined in Task 5
    }

    /// <summary>Current page number and total, both in visual pages, for the reader's position label.</summary>
    private void RecountVisualPages()
    {
        if (DisplayText is not { } text || _metrics is not { } metrics || text.Length == 0)
        {
            _visualPageCount = 0;
            PageCount = 0;
            PageNumber = 0;
            return;
        }

        _visualPageCount = _paginator.CountPages(text, metrics);
        PageCount = _visualPageCount;
        var (index, _) = _paginator.PageContaining(text, _pageStartOffset, metrics);
        PageNumber = index + 1;
    }
```

Update `UpdateNavigation()` to visual-page bounds:

```csharp
    private void UpdateNavigation()
    {
        var text = DisplayText;
        var hasMetrics = _metrics is not null;
        CanGoPrevious = hasMetrics && _pageStartOffset > 0;
        CanGoNext = hasMetrics && !string.IsNullOrEmpty(text) && _metrics is { } m
            && _pageStartOffset + _paginator.MeasurePage(text, _pageStartOffset, m) < text!.Length;
    }
```

In `SetReadingMetrics` (Task 3), after `ReSlice()`, add renumber + nav refresh and reset the back-stack (offsets computed under old metrics are stale):

```csharp
        _visitedPageStarts.Clear();
        RecountVisualPages();
        UpdateNavigation();
```

In `BuildContinuousText`'s re-slice (Task 3), after `ReSlice()`, also `RecountVisualPages(); UpdateNavigation();` so the label/nav track flow rebuilds.

**Provisional stub for Task 5's method** (so this task builds and tests run): add a temporary

```csharp
    private Task TranslateVisiblePageAsync(ReadingDirection direction) => Task.CompletedTask;
```

Task 5 replaces this body.

Note: `TranslationPercentLabel` and `CurrentPageLabel` already read `PageNumber`/`PageCount`; no change needed. The old Core-page `_currentIndex` field remains (Task 5 keeps it in sync for prefetch).

- [ ] **Step 4: Update existing page-count assertions to visual pages**

Existing tests assert Core page counts (`PageCount == 2`, `CurrentPageLabel == "Page 1/2"`) that no longer hold without metrics (before `SetReadingMetrics`, `PageCount` is 0). For each such test that needs a page count/label, add a `vm.SetReadingMetrics(400, 400, Avalonia.Media.Typeface.Default);` call after `OpenAsync` and update the expected numbers to the visual count (a short one-page PDF → `PageCount == 1`, `"Page 1/1"`), marking those tests `[AvaloniaFact]`. Tests that only assert text content (`OriginalText`/`TranslationText`/`DisplayText`) need no change.

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj`
Fix each failing count/label assertion as above until green.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj`
Expected: PASS (new visual-navigation tests + updated existing tests).

- [ ] **Step 6: Commit**

```bash
git add src/BabelRead.App/ViewModels/ReaderViewModel.cs tests/BabelRead.App.Tests/ReaderViewModelTests.cs
git commit -m "Navigate and number by visual pages in ReaderViewModel"
```

---

## Task 5: VM — drive translation/prefetch from the visual page

The reader now moves by visual pages, but translation and prefetch still run on Core pages. Derive the Core page from the current visual offset and translate/prefetch that page, so Off ("current + 2 ahead") and on-demand translation track where the reader actually is.

**Files:**
- Modify: `src/BabelRead.App/ViewModels/ReaderViewModel.cs`
- Test: `tests/BabelRead.App.Tests/ReaderViewModelTests.cs`

**Interfaces:**
- Consumes: `_pageStartOffset`, `_segmentCharOffsets`, `_pageFirstSegment`, existing `TranslateCurrentAsync`, `SchedulePrefetch`, `_currentIndex`, `GetPageAsync`.
- Produces: `Task TranslateVisiblePageAsync(ReadingDirection direction)` — maps `_pageStartOffset` → segment → Core page, sets `_currentIndex`, translates that Core page (reusing `TranslateCurrentAsync`) if it has untranslated text, and schedules prefetch. Replaces the Task 4 stub.

- [ ] **Step 1: Write the failing test**

Add to `ReaderViewModelTests.cs` (headless). Reuse the Off-mode helper style already in the file (the `RecordingPrefetch`/count pattern used by the existing Off tests — follow that file's existing approach for asserting prefetch/translation span). Concretely:

```csharp
    [AvaloniaFact]
    public async Task Reading_into_a_later_visual_page_translates_the_core_page_it_lands_on()
    {
        var vm = CreateViewModel();
        // Enough text that page 2's visual page starts inside a later Core page than page 1.
        await vm.OpenAsync(CreatePdf(
            string.Join(" ", Enumerable.Repeat("alpha", 400)),
            string.Join(" ", Enumerable.Repeat("omega", 400))));
        vm.SetReadingMetrics(400, 200, Avalonia.Media.Typeface.Default);

        // Advance visual pages until the visible text reaches the second document page's words.
        var guard = 0;
        while (vm.CanGoNext && vm.VisiblePageText?.Contains("omega", StringComparison.Ordinal) != true && guard++ < 200)
        {
            await vm.NextPageAsync();
        }

        Assert.Contains("omega", vm.VisiblePageText!, StringComparison.Ordinal);
        Assert.Equal(ReaderState.Content, vm.State);
        // The fake client echoes the source, so the visible page shows the translated (echoed) text.
        Assert.Contains("omega", vm.DisplayText!, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj --filter "FullyQualifiedName~later_visual_page"`
Expected: FAIL — the stub `TranslateVisiblePageAsync` does nothing, so a later Core page never translates.

- [ ] **Step 3: Write minimal implementation**

Replace the Task 4 stub with:

```csharp
    /// <summary>Map the current visual page to the Core page it starts in, make that the active page, and
    /// translate/prefetch it — so on-demand translation and the Off cap follow the reader's real position.</summary>
    private async Task TranslateVisiblePageAsync(ReadingDirection direction)
    {
        if (_document is null || _reader is null || _metrics is null)
        {
            return;
        }

        var coreIndex = CorePageForOffset(_pageStartOffset);
        _currentIndex = coreIndex;

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

            if (page.HasText && !IsFullyTranslated(page))
            {
                await TranslateCurrentAsync(page, token).ConfigureAwait(true);
            }
            else if (State != ReaderState.Content && page.HasText)
            {
                State = ReaderState.Content;
                StatusMessage = null;
            }

            await PersistLastReadPageAsync(coreIndex).ConfigureAwait(true);
            SchedulePrefetch(direction);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The Core page index whose segments contain the char offset in the flow.</summary>
    private int CorePageForOffset(int charOffset)
    {
        if (_segmentCharOffsets.Length == 0 || _pageFirstSegment.Length == 0)
        {
            return 0;
        }

        // Which segment holds this char offset...
        var i = Array.BinarySearch(_segmentCharOffsets, charOffset);
        var segment = i >= 0 ? i : Math.Clamp(~i - 1, 0, _segmentCharOffsets.Length - 1);

        // ...and which Core page that segment begins within (last page whose first segment <= segment).
        var page = 0;
        for (var p = 0; p < _pageFirstSegment.Length; p++)
        {
            if (_pageFirstSegment[p] <= segment)
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
```

(The segment/page lookup mirrors the existing `PageStartCharOffset` / `SegmentAtCharOffset` helpers; reuse those private helpers if their signatures already provide the mapping, instead of duplicating `CorePageForOffset`.)

Wire initial position on open: in `OpenAsync`, the resume path currently calls `GoToPageAsync(startIndex, ...)`. Replace that with visual-page initialization — set `_pageStartOffset` to the start Core page's char offset and run the visual pipeline:

```csharp
            _pageStartOffset = PageStartCharOffset(startIndex);
            _visitedPageStarts.Clear();
            ReSlice();
            RecountVisualPages();
            UpdateNavigation();
            await TranslateVisiblePageAsync(ReadingDirection.Forward).ConfigureAwait(true);
```

(`PageStartCharOffset(int)` already exists and returns the char offset of a Core page's first segment.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj`
Expected: PASS — including the existing Off-mode tests, which now measure the span relative to the derived Core page. If an Off test asserted an exact Core index that shifted, update it to assert the span/count (current + 2 ahead) rather than a hard-coded index.

- [ ] **Step 5: Commit**

```bash
git add src/BabelRead.App/ViewModels/ReaderViewModel.cs tests/BabelRead.App.Tests/ReaderViewModelTests.cs
git commit -m "Drive translation and prefetch from the visual page's Core page"
```

---

## Task 6: View — clipped page panel, metrics push, keyboard turns

Swaps the scrolling surface for a clipped page and feeds the VM real metrics.

**Files:**
- Modify: `src/BabelRead.App/Views/ReaderView.axaml`
- Modify: `src/BabelRead.App/Views/ReaderView.axaml.cs`
- Test: `tests/BabelRead.App.Tests/ViewSmokeTests.cs`

**Interfaces:**
- Consumes: `ReaderViewModel.VisiblePageText`, `ReaderViewModel.SetReadingMetrics` (Tasks 3-5).

- [ ] **Step 1: Update the XAML**

In `ReaderView.axaml`, replace the `ScrollViewer` (`Name="ReadingScroll"` ... `</ScrollViewer>`) block with a clipped panel that renders one page. Keep the same inset/column/font bindings:

```xml
    <Panel Name="ReadingSurface" IsVisible="{Binding IsContentVisible}" ClipToBounds="True">
      <SelectableTextBlock Name="PageText" Text="{Binding VisiblePageText}"
                           FlowDirection="{Binding ReadingFlowDirection}"
                           AutomationProperties.Name="Page text"
                           TextWrapping="Wrap" TextAlignment="Justify"
                           Margin="24,72,24,56" MaxWidth="720"
                           HorizontalAlignment="Center" VerticalAlignment="Top"
                           FontSize="{Binding ReadingFontSize}" LineHeight="{Binding ReadingLineHeight}" />
    </Panel>
```

(`VerticalAlignment="Top"`: a page fills from the top; the short-last-page centering behavior is no longer needed since a page is measured to fit.)

- [ ] **Step 2: Update the code-behind**

In `ReaderView.axaml.cs`:

- Change the field `_readingScroll` (type `ScrollViewer`) to `_readingSurface` (type `Panel`) and `FindControl<Panel>("ReadingSurface")`. Update the `SizeChanged` subscription and `.Focus()` calls accordingly. Remove `ScrollBarGutter` usage (no scrollbar now) — the column width becomes `Min(Bounds.Width - 2*InsetX, ReadingColumnMaxWidth)`.
- In `ScheduleReflow`, after computing `size`, push metrics to the VM instead of (or in addition to) `ReflowForViewportAsync`. Replace `await ViewModel.ReflowForViewportAsync(size.Width, size.Height);` with:

```csharp
                var typeface = new Typeface(this.FindControl<SelectableTextBlock>("PageText")!.FontFamily);
                ViewModel.SetReadingMetrics(size.Width, size.Height, typeface);
```

Add `using Avalonia.Media;` for `Typeface`.

- In `OnViewModelPropertyChanged`, remove the `PageNumber` → `_readingScroll.Offset = default` branch (no scrolling). The block that references `_readingScroll.Offset` is deleted.
- The keyboard handler already calls `ViewModel.PreviousPageAsync()` / `NextPageAsync()`, which now move visual pages — no change needed there.

- [ ] **Step 3: Replace the scroll smoke test with a page-fit smoke test**

In `ViewSmokeTests.cs`, replace `A_short_page_is_vertically_centred_in_the_reading_window` with a test that the page surface does not scroll and renders the visible page:

```csharp
    [AvaloniaFact]
    public void The_reading_surface_shows_the_visible_page_and_does_not_scroll()
    {
        var store = new InMemoryTranslationStore();
        var vm = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(new FakeChatClient()), store),
            store,
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(Path.GetTempPath(), $"{System.Guid.NewGuid():n}.json")));
        vm.ShowingTranslation = false;
        vm.VisiblePageText = "A page of text shown without any scrollbar.";
        vm.State = ReaderState.Content;

        var view = new ReaderView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(view.GetVisualDescendants().OfType<ScrollViewer>()); // reading surface no longer scrolls
        var text = view.FindControl<SelectableTextBlock>("PageText")!;
        Assert.Equal(vm.VisiblePageText, text.Text);
    }
```

For this test to set `vm.VisiblePageText` directly, ensure the `[ObservableProperty]` setter is accessible from tests (the generated `VisiblePageText` property is public; assigning it is fine). Keep the `SettingsView` scroll test unchanged (Settings still scrolls).

- [ ] **Step 4: Build and run the App tests**

Run: `dotnet build && dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj`
Expected: 0 warnings / 0 errors; PASS. Fix any remaining reference to `_readingScroll` or `ReadingScroll`.

- [ ] **Step 5: Commit**

```bash
git add src/BabelRead.App/Views/ReaderView.axaml src/BabelRead.App/Views/ReaderView.axaml.cs tests/BabelRead.App.Tests/ViewSmokeTests.cs
git commit -m "Render one clipped visual page and push reading metrics from the view"
```

---

## Task 7: Remove dead scroll-anchor code and reconcile Core reflow

Cleanup: the char-offset-to-scroll anchor and `ReadingCharOffset` are superseded. `ReflowForViewportAsync` is no longer called from the view; decide its fate.

**Files:**
- Modify: `src/BabelRead.App/ViewModels/ReaderViewModel.cs`
- Modify: `tests/BabelRead.App.Tests/ReaderViewModelTests.cs` (only if a test referenced removed members)

**Interfaces:**
- Removes: `ReadingCharOffset` observable property and its usages; the scroll-anchor helpers used only by the old scroll flow (`PageStartCharOffset` is still used by Task 5's open path — keep it; remove only genuinely unused helpers).

- [ ] **Step 1: Find what is now unused**

Run: `grep -n "ReadingCharOffset\|ReflowForViewportAsync\|SegmentAtCharOffset" src/BabelRead.App/ViewModels/ReaderViewModel.cs src/BabelRead.App/Views/*.cs`
Expected: `ReadingCharOffset` has no remaining view binding (removed in Task 6); `ReflowForViewportAsync` has no caller.

- [ ] **Step 2: Remove `ReadingCharOffset`**

Delete the `[ObservableProperty] private int _readingCharOffset;` and any code that sets it (e.g. in the old `GoToPageAsync`). Since warnings are errors, an unused private field will fail the build and confirm removal is complete.

- [ ] **Step 2b: Reroute `RetryAsync` and `JumpToPageAsync` through the visual pipeline**

`GoToPageAsync` (the old Core-page scroll path) is still called by `RetryAsync` and `JumpToPageAsync`, and Task 7 Step 2 removes the `ReadingCharOffset` it set. Point both at the visual pipeline instead:
- `RetryAsync`: re-run `await TranslateVisiblePageAsync(ReadingDirection.Forward);` (keeps the current `_pageStartOffset`).
- `JumpToPageAsync(oneBasedPageNumber)`: treat the number as a **visual** page — set `_pageStartOffset` by walking `MeasurePage` from 0 that many pages (or via a small helper `StartOffsetOfVisualPage(int)`), clear `_visitedPageStarts`, then `ReSlice(); RecountVisualPages(); UpdateNavigation(); await TranslateVisiblePageAsync(...)`.
Then delete `GoToPageAsync` if nothing else references it (the build's warnings-as-errors will confirm).

- [ ] **Step 3: Decide `ReflowForViewportAsync`**

`ReflowForViewportAsync` sized Core pages to the viewport by reopening the document. Visual pages no longer depend on it, but Core page size still bounds translation batches and the Off span. Keep the method for Core sizing but stop reopening on every resize: it is currently uncalled, so either (a) delete it and let Core pages keep `DefaultCharsPerPage`, or (b) call it once from `OpenAsync` at the initial viewport. **Choose (a)** for this pass (YAGNI): delete `ReflowForViewportAsync`, the `_viewportWidth`/`_viewportHeight` fields, and the `IReflowableDocumentReader` viewport plumbing calls from the VM if they become unused. Leave `SegmentPaginator`/`_charsPerVirtualPage` and the readers as-is (Core pages keep the default size). If deleting `ReflowForViewportAsync` orphans `TranslationGrowthAllowance`/`PaginationBaseline`, remove those too.

- [ ] **Step 4: Build and run the full suite**

Run: `dotnet build && dotnet test`
Expected: 0 warnings / 0 errors; all projects PASS (Core, App, Integration).

- [ ] **Step 5: Manual smoke (documented, not automated)**

Launch the app, open an EPUB and a PDF, and confirm: no scrollbar; Left/Right and PageUp/PageDown turn pages; text fills top-to-bottom and a paragraph continues across the break; `Page N/M` updates; Ctrl +/- re-paginates in place; resizing re-paginates without losing position; toggling original/translation keeps the page.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Remove scroll-anchor code superseded by visual pagination"
```

---

## Notes for the implementer

- **Headless text measurement:** `TextLayout` under `[AvaloniaFact]` produces real line metrics in this repo (see `ViewSmokeTests` centering assertions), so paginator tests exercise genuine wrapping. Keep assertions structural (exact cover, ≤ maxLines, forward progress) rather than pixel-exact.
- **Do not touch** `TranslationService`, `PrefetchCoordinator`, `JsonTranslationStore`, or the readers' segment logic. All translation behavior is preserved by keeping the Core page/segment model and mapping the visual offset onto it.
- **DisplayText stability:** because `_orderedSegments` is pagination-independent, `DisplayText` is byte-identical regardless of Core page size, so a stored `_pageStartOffset` remains valid across any Core-page change and across translation rebuilds of content before the offset.
