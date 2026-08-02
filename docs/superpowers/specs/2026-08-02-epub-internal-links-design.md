# Navigable Internal EPUB Links — Design

**Date:** 2026-08-02
**Status:** Approved (pending written review)

## Goal

Preserve the internal hyperlinks in an EPUB (table-of-contents entries, footnote/endnote
references, cross-references) and let the reader follow them to the target content, with a
browser-style way back. Links are clickable while reading the **original** text; the
translated view stays plain.

## Background / current state

- `EpubDocumentReader.HtmlToText` flattens each spine item's HTML to plain text with a chain
  of regexes. Every tag is stripped, so both `<a href>` link spans **and** the `id=`/`<a name>`
  anchor targets they point at are discarded today.
- The whole reading model downstream is plain `string`: `Document.Segments` are strings, the
  view-model concatenates them into the continuous flow (`DisplayText`), and the reading pane
  renders a plain-string slice (`VisiblePageText`) in a `SelectableTextBlock`.
- Segments are the translation-cache keys. The reader comments stress they must be **stable**:
  if the extracted text changes at all, every cached translation orphans.

## Hard constraint

Preserving links **must not change the extracted segment text by a single character.** This
rules out swapping in an HTML-DOM parser (its text extraction would not match byte-for-byte)
and drives the sentinel approach below.

## Scope

**In:** EPUB only; internal links only (targets inside the same book); clickable in the
original view only; browser-style Back after a jump.

**Out:** PDF link annotations; external links (`http(s):`, `mailto:`) — rendered as plain,
non-clickable text; clickable links inside the translated text.

## Architecture

Four layers, each independently testable:

1. **Parse** (Core): recover link spans and anchor positions from EPUB HTML *without changing
   the text*.
2. **Map** (Core): turn chapter-text positions into `(segmentIndex, offsetInSegment)`, resolve
   hrefs to absolute anchor keys, expose `Links`/`Anchors` on `Document`.
3. **View-model**: map links into the current visible page (original view), follow a link, and
   a link-return stack for Back.
4. **View**: render the original-view page as inline runs with clickable links; a Back control
   plus keys.

### 1. Parse links without changing the text

Keep `HtmlToText` producing the identical string. To recover positions through its
normalization, run a variant that first injects **private-use sentinel characters** at the
points of interest, then runs the *same* pipeline:

- `` — marks a `<a href>` open (paired, by order, with its href).
- `` — marks the matching `</a>` close.
- `` — marks an anchor: any element `id="..."` or `<a name="...">` (paired, by order,
  with its id).

Sentinels are private-use scalars: they are not tags, not whitespace, not entities, so none of
the existing regexes (`ScriptStyleRegex`, `BrTagRegex`, `BlockTagRegex`, `TagRegex`, the
whitespace collapsers) nor `HtmlDecode` touch them. After the pipeline runs, scan the output
for sentinels to record each one's offset (subtracting the length of sentinels seen so far),
pairing them with the hrefs/ids captured during injection, then remove the sentinels to obtain
the final chapter text.

Output per chapter:
- `LinkSpan { int Start; int Length; string RawHref }` — `Start`/`Length` are offsets into the
  final chapter text (the text between the open and close sentinels).
- `Anchor { string Id; int Offset }` — offset into the final chapter text.

**Guard:** a test asserts the sentinel-stripped text equals `HtmlToText(html)` for a corpus of
representative chapters, so segments never shift and translations never orphan.

**Injection detail:** a light scan (regex over `<a ...>`, `</a>`, and `id=`/`name=` attributes)
locates insertion points in the raw HTML and splices sentinels in before normalization. The
scan need not be a full HTML parser — it only has to find tag boundaries and the two attributes,
which the existing regex style already does.

### 2. Map to the reading model

- **Segmentation offsets:** `SplitIntoSegments` (large-block splitting + short-block coalescing)
  currently returns `IReadOnlyList<string>`. Add a parallel path that also yields, per emitted
  segment, its `(startInChapterText, length)` range, so any chapter-text offset maps to
  `(segmentIndexWithinChapter, offsetInSegment)`. Segments are concatenated across chapters into
  `Document.Segments` in reading order, giving a global `segmentIndex`.
- **Href resolution:** resolve each `RawHref` against the owning spine item's file path:
  - `#frag` → `{thisSpinePath}#frag`
  - `file.xhtml#frag` (possibly with `../`, subdirs) → `{resolvedSpinePath}#frag`
  - `file.xhtml` (no fragment) → `{resolvedSpinePath}` (chapter start)
  - `http(s):`/`mailto:`/protocol-relative → dropped (external).
- **Anchor keys:** each `Anchor` becomes key `{spinePath}#{id}`; each chapter also registers a
  bare `{spinePath}` key at its first segment (offset 0) for whole-file links.

Expose on `Document`:
- `IReadOnlyList<DocumentLink> Links` where `DocumentLink { int SegmentIndex; int Start; int
  Length; string TargetKey }` (`TargetKey` is the resolved absolute key).
- `IReadOnlyDictionary<string, LinkTarget> Anchors` where `LinkTarget { int SegmentIndex; int
  Offset }`.

Links whose `TargetKey` has no matching anchor are dropped (rendered as plain text) — a broken
internal reference should not look clickable.

PDFs return empty `Links`/`Anchors`; nothing else changes for them.

### 3. View-model

- Maintain a **link layout** for the current page in the original view: for each `DocumentLink`,
  its flow char range is `_segmentCharOffsets[SegmentIndex] + Start .. + Length`. Intersect with
  the current visible slice `[_pageStartOffset, _pageStartOffset + consumed)` and expose the ones
  that fall inside as **slice-relative** ranges plus their `TargetKey`. Recomputed on
  re-slice/navigation while `ShowingTranslation` is false; empty while it is true.
- `FollowLinkAsync(string targetKey)`: look up the anchor → `(segmentIndex, offset)` → flow
  offset via `_segmentCharOffsets`; **push the current `_pageStartOffset` onto a link-return
  stack**; set the new offset and run the existing visual-page-change path (re-slice, renumber,
  translate/prefetch the target's Core page). Works from either view; the target content is what
  matters.
- `GoBackFromLinkAsync()` + `CanGoBackFromLink`: pop the link-return stack and navigate there.
  This stack is **separate** from the visual-page back-stack (`_visitedPageStarts`); it only
  records link jumps.
- The link-return stack entries are flow offsets and are remapped alongside the other offsets
  when the flow is rebuilt (reusing the existing `RemapOffset` path).

### 4. View

- **Original view:** render the visible page as `SelectableTextBlock` **inlines** built from the
  link layout — a `Run` for each plain gap and a styled, clickable `Run` (accent brush,
  underline) for each link span, wired to `FollowLinkAsync(targetKey)` via a pointer handler.
  Text selection continues to work across runs.
- **Translation view:** unchanged plain-string path (`Text="{Binding VisiblePageText}"`).
- The view switches between the two render paths on `ShowingTranslation`.
- **Back:** a toolbar Back control (e.g. a ‹ back-arrow chip) shown only when
  `CanGoBackFromLink`, plus **Backspace** and **Alt+Left** in the reader key handler, bound to
  `GoBackFromLinkAsync`.

## Data flow

```
EPUB html ──inject sentinels──▶ HtmlToText pipeline ──scan+strip──▶ (chapter text, LinkSpans, Anchors)
   (segment text is byte-identical to today's output — guarded by test)
        │
        ▼  SplitIntoSegments (+ offset tracking)     href resolution (spine paths)
   Document.Segments  +  Document.Links[(segIdx,start,len,targetKey)]  +  Document.Anchors[key→(segIdx,off)]
        │
        ▼  VM: original view only
   link layout for visible slice  ──click──▶ FollowLinkAsync(targetKey)
                                               push return-stack; jump to Anchors[key] flow offset
        │
        ▼  View
   SelectableTextBlock inlines (plain + clickable link runs)   +   Back control / Backspace / Alt+Left
```

## Error handling / edge cases

- **Unresolvable target** (dangling `#frag`, missing file): link dropped → plain text.
- **External href:** dropped → plain text.
- **Link spanning a paragraph break** (`\n\n` inside its range): allowed; the span covers it.
- **Overlapping/nested `<a>`:** malformed EPUBs — take the outermost; ignore a stray close.
- **Anchor at end of chapter / empty target file:** resolves to the nearest segment; never
  throws.
- **Link visible only partially on a page:** render the on-page portion as clickable; the whole
  link resolves to the same target regardless of which part is tapped.
- **Following a link while in translation view:** allowed (jump lands on the content); the link
  itself is only *rendered* clickable in the original view.

## Testing

**Core**
- Sentinel extraction returns correct link spans and anchor offsets for representative HTML
  (inline `<a>`, TOC list, footnote ref + note target, `id` on a block element, `<a name>`).
- **Exact-text guard:** stripped text == `HtmlToText(html)` across a corpus (segment stability).
- Href resolution: `#frag`, `sub/file.xhtml#frag`, `../file.xhtml`, bare file, external dropped.
- Offset-through-segmentation: a chapter position maps to the right `(segmentIndex, offset)` after
  large-block split and short-block coalesce.

**App (view-model)**
- Links falling inside the visible slice are exposed as correct slice-relative ranges in original
  view; none exposed in translation view.
- `FollowLinkAsync` jumps to the target content and pushes the return stack; `GoBackFromLink`
  returns to the exact prior offset; `CanGoBackFromLink` reflects the stack.
- Return-stack offsets survive a flow rebuild (toggle / landing translation) via `RemapOffset`.

**App (view smoke)**
- Original view renders a clickable link run for a document with a link; translation view renders
  plain text with no link run.

## Open implementation notes (resolved in the plan)

- Exact VersOne.Epub property for a spine item's file path (for href resolution).
- The precise `SelectableTextBlock` inline click wiring in Avalonia (pointer handler on a `Run`
  vs. an `InlineUIContainer`), chosen to keep text selection intact.
