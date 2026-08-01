# Page-by-page reading (viewport-fitted pagination)

**Date:** 2026-08-01
**Status:** Approved design, pending spec review

## Context

The reader currently renders the whole document as one continuous `SelectableTextBlock`
inside a `ScrollViewer` (`ReaderView.axaml`). `ReaderViewModel.DisplayText` holds the entire
book's flow (each paragraph translated where the store holds it, original otherwise). The
Prev/Next buttons scroll that flow to a Core page's first-paragraph anchor via
`ReadingCharOffset`; the reader can also free-scroll.

The user wants a **page-by-page** experience with **no scrollbar and no free scrolling**:
each screen shows exactly what fits the window, and turning advances one screenful. Text
must stay **continuous** — a paragraph fills to the bottom of a page and continues at the
top of the next (mid-paragraph split, printed-book style), not whole-paragraphs-per-page.

## Goals

- Each visual page shows exactly the text that fits the current window at the current font.
- A paragraph splits across the page break and resumes at the top of the next page.
- No scrollbar, no free scroll. Turn with Prev/Next buttons and arrow keys.
- Resizing the window or changing the font re-paginates while preserving reading position.
- Translation, prefetch, Off/Gentle/FullSpeed, and progress % behavior are unchanged.

## Non-goals

- No mid-paragraph *segment* splitting for translation. Segments remain whole paragraphs.
- No pixel-perfect justification tuning beyond what Avalonia's `TextLayout` produces.
- No animated page-turn transition (can come later).
- No change to the store, translation service, or prefetch coordinator.

## Two notions of "page"

This is the central structural decision. We keep the existing model and layer a new one on top.

1. **Core page (unchanged).** Whole-paragraph groups produced by `SegmentPaginator`, sized by
   the `_charsPerVirtualPage` character heuristic. This stays the unit for **translation
   batching, on-demand translation, background prefetch, the Off/Gentle/FullSpeed cap
   (current + 2 ahead), progress %, and store keys.** None of the earlier segment-cost or
   durability work is disturbed. `ReflowForViewportAsync` / `IReflowableDocumentReader`
   continue to exist for this purpose.

2. **Visual page (new).** A viewport-fitted slice of `DisplayText`, defined by rendered text
   layout, used **only for display and navigation.** Prev/Next move visual pages.

The two are reconciled by mapping the current visual page's start char-offset →
segment → Core page, so prefetch/Off still act relative to where the reader actually is.

## Approach: incremental, measure-per-page

Avalonia has no paginated-text control, so we measure with
`Avalonia.Media.TextFormatting.TextLayout`.

For a page starting at char-offset `start` into `DisplayText`:

1. Build a `TextLayout` over `DisplayText[start..]` with `maxWidth = columnWidth` and
   `maxHeight = viewportHeight` (and the active typeface / font size / line height).
2. The layout yields exactly the `TextLines` that fit. Summing their `TextRange` lengths
   gives `consumed` — the char count on this page.
3. The page's text is `DisplayText[start .. start + consumed]`; the next page starts at
   `start + consumed`.

Measuring only the current page (not the whole book) means each turn re-measures forward
from a known-good offset, so pagination stays correct as translated text (which differs in
length from the original) lands ahead of the reader.

Rejected alternative — *whole-document line index up front*: simpler random access, but
re-measures the entire document on every resize, font change, and every time a translation
changes text length. Not worth it.

## Components

### `ReadingPaginator` (new, App layer)

Pure-ish measurement helper. No VM or view state.

```
sealed class ReadingPaginator
{
    // Returns the char length that fits one page starting at `start`.
    int MeasurePage(string text, int start, ReadingPageMetrics metrics);

    // Walks forward from 0 to find the page index (and its start offset)
    // whose range contains `charOffset` — used for jump-to-page / resume.
    (int pageIndex, int pageStart) PageContaining(string text, int charOffset, ReadingPageMetrics metrics);
}

readonly record struct ReadingPageMetrics(
    double ColumnWidth, double ViewportHeight,
    double FontSize, double LineHeight, Typeface Typeface, FlowDirection FlowDirection);
```

`MeasurePage` builds a `TextLayout` and sums fitted line lengths. It advances by at least
one line so a single over-long line can never stall progress.

### `ReaderViewModel` changes

- New state: `_pageStartOffset` (char offset of the current visual page's start). `DisplayText`
  **remains the whole-document flow** (the paginator measures against it). A **new
  `VisiblePageText` property** holds the current page slice, and the view binds to it. Keeping
  the two separate means translation rebuilds update `DisplayText` once and re-slicing is a
  cheap substring, with no ambiguity about which property is whole-doc vs page.
- New inputs from the view: current `ReadingPageMetrics` (column width, viewport height,
  typeface) pushed on resize/font change, analogous to today's `ReflowForViewportAsync`.
- Navigation:
  - `NextVisualPage`: `start += MeasurePage(...)`, re-slice. Clamp at end of text.
  - `PreviousVisualPage`: recompute the previous start. Because pages are contiguous by
    offset, maintain a small stack of visited page-start offsets for O(1) back; on a cold
    jump, use `PageContaining` to rebuild.
  - `CanGoPrevious` / `CanGoNext` reflect visual-page bounds (start > 0 / end < length).
- Prefetch/Off: derive the Core page from `_pageStartOffset` (offset → segment via
  `_segmentCharOffsets`, then segment → Core page via `_pageFirstSegment`) and feed that to
  the existing `SchedulePrefetch` / Off logic. On-demand translation triggers for the Core
  page(s) the visible slice overlaps.
- Rebuild-on-translation: when `DisplayText` is rebuilt (translation lands, toggle), re-slice
  the current page from `_pageStartOffset` so newly translated text on screen appears. Text
  before `_pageStartOffset` may change length; the current page is re-measured from its
  offset, which stays valid because already-read (earlier) content is already translated and
  stable.

### View changes (`ReaderView.axaml` / `.axaml.cs`)

- Replace the `ScrollViewer` with a clipped `Panel` (`ClipToBounds="True"`, no scrollbar).
- Bind the reading `SelectableTextBlock` to the current page slice.
- Push `ReadingPageMetrics` to the VM on `SizeChanged` / font change (reuse the existing
  debounced `ScheduleReflow` plumbing; it already computes column width and viewport height,
  minus the now-unneeded scrollbar gutter).
- Arrow keys / PageUp-PageDown map to Prev/Next visual page (extends existing keyboard nav).

## Data flow

```
DisplayText (whole-doc flow, rebuilt on translation/toggle)
        │
        ▼
ReadingPaginator.MeasurePage(DisplayText, _pageStartOffset, metrics)
        │  → consumed chars
        ▼
VisiblePageText = DisplayText[start .. start+consumed]   → SelectableTextBlock
        │
        ▼
offset → segment → Core page  →  SchedulePrefetch / Off cap / progress (unchanged)
```

## Edge cases

- **Empty / no-text document:** VisiblePageText null → existing NoText/empty states apply.
- **Metrics not yet known** (first layout before SizeChanged): show from offset 0 with a
  sensible default; re-slice when real metrics arrive.
- **Over-long single line** (e.g., an unbroken URL wider than the column): `MeasurePage`
  guarantees forward progress of at least one line.
- **Translation changes current-page length mid-read:** re-slice from `_pageStartOffset`;
  content before the offset is already stable, so the current page does not jump.
- **Font increased so nothing fits:** clamp to at least one line per page.
- **Resize during read:** re-paginate from `_pageStartOffset` so the reader stays on
  approximately the same text.

## Testing

- **`ReadingPaginatorTests` (headless Avalonia, like `ViewSmokeTests`):**
  - Concatenation of successive page slices equals the full text (no loss, no overlap).
  - Every page except the last fills near the viewport height; no page exceeds it.
  - Page boundaries fall on line ends (mid-paragraph splits occur at line breaks, not
    mid-word beyond Avalonia's own wrapping).
  - `PageContaining` returns the page whose range brackets a given offset.
  - Forward progress guaranteed for an over-long unbreakable token.
- **`ReaderViewModelTests`:**
  - Next/Prev advance and retreat visual pages; `CanGoNext/Previous` at the ends.
  - The Core page derived from the visual offset drives prefetch (Off cap still
    "current + 2 ahead" relative to the reading position).
  - Toggling original/translation re-slices the current page.
- **Regression:** existing translation, prefetch, Off-mode, progress, and durability tests
  remain green (Core model untouched).

## Rollout / sequencing

1. `ReadingPaginator` + tests (pure measurement, no VM wiring).
2. VM: page-start state, slicing property, Next/Prev visual-page commands, metrics input.
3. VM: derive Core page from offset; wire prefetch/Off/progress to it.
4. View: swap ScrollViewer → clipped Panel, bind slice, push metrics, keyboard turns.
5. Remove now-dead scroll-anchor code paths (`ReadingCharOffset` scroll wiring) if fully
   superseded; keep `ReflowForViewportAsync` for Core-page sizing.
