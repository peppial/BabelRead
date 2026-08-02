# Navigable Internal EPUB Links — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve an EPUB's internal hyperlinks and let the reader follow them to the target content (clickable in the original view), with a browser-style Back.

**Architecture:** A position-tracking tokenizer recovers `<a href>` spans and `id`/`name` anchors from each chapter's HTML. The extracted **segment text stays byte-identical to today's `HtmlToText`** — links/anchors are used only when the tokenizer's text reconstruction matches `HtmlToText` for that chapter, otherwise that chapter's links are dropped. Positions are mapped through segmentation to `(segmentIndex, offset)`, hrefs resolved to absolute anchor keys, and exposed on `Document`. The view-model maps links into the visible slice (original view only), follows a link (pushing a return stack), and offers Back. The view renders the original-view page as `SelectableTextBlock` inlines with clickable link runs, hit-tested via a `SelectableTextBlock` subclass.

**Tech Stack:** .NET 10, C# (file-scoped namespaces, warnings-as-errors), Avalonia 12.1.0, VersOne.Epub 3.3.6, CommunityToolkit.Mvvm, xUnit + Avalonia.Headless.XUnit.

## Global Constraints

- **Segments must never change.** EPUB segment text is always `HtmlToText(html)` exactly as today; a chapter's links are used only if the tokenizer reconstructs that same text, else dropped. No cached translation may be orphaned by this feature.
- **EPUB only.** PDF returns empty `Links`/`Anchors`; no other PDF behavior changes.
- **Internal links only.** `http(s):`, `mailto:`, protocol-relative, and any href with no matching anchor are dropped (rendered as plain text).
- **Clickable in the original view only.** Translation view keeps the existing plain-string render path; no link runs.
- **Follow works from either view; Back is browser-style** on a return stack separate from the visual-page back-stack (`_visitedPageStarts`), and its entries are remapped by the existing `RemapOffset` on flow rebuild.
- Warnings are errors: no unused usings/fields; `dotnet build` must be 0/0.

---

## File Structure

- Create `src/BabelRead.Core/Documents/EpubLinkExtractor.cs` — the tokenizer (text + link spans + anchors).
- Create `src/BabelRead.Core/Domain/DocumentLink.cs` — `DocumentLink`, `LinkTarget` types.
- Modify `src/BabelRead.Core/Domain/Document.cs` — add `Links`, `Anchors`.
- Modify `src/BabelRead.Core/Documents/EpubDocumentReader.cs` — call the extractor, map through segmentation, resolve hrefs, populate `Links`/`Anchors`; track segment ranges.
- Modify `src/BabelRead.App/ViewModels/ReaderViewModel.cs` — link layout for the visible slice, `FollowLinkAsync`, `GoBackFromLinkAsync`, `CanGoBackFromLink`, return stack.
- Create `src/BabelRead.App/Controls/LinkableTextBlock.cs` — `SelectableTextBlock` subclass exposing a click→char-index→link hook and building link-styled inlines.
- Modify `src/BabelRead.App/Views/ReaderView.axaml` (+`.axaml.cs`) — dual render path, Back control, Backspace/Alt+Left.
- Tests: `tests/BabelRead.Core.Tests/Documents/EpubLinkExtractorTests.cs`, additions to `EpubDocumentReaderTests.cs`, `tests/BabelRead.App.Tests/ReaderViewModelTests.cs`, `tests/BabelRead.App.Tests/ViewSmokeTests.cs`, and a link sample in `tests/BabelRead.TestSupport/SampleDocuments.cs`.

---

## Task 1: EPUB link/anchor tokenizer

**Files:**
- Create: `src/BabelRead.Core/Documents/EpubLinkExtractor.cs`
- Test: `tests/BabelRead.Core.Tests/Documents/EpubLinkExtractorTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public readonly record struct RawLinkSpan(int Start, int Length, string Href);
  public readonly record struct RawAnchor(string Id, int Offset);
  public readonly record struct ExtractedChapter(
      string Text, IReadOnlyList<RawLinkSpan> Links, IReadOnlyList<RawAnchor> Anchors);
  public static ExtractedChapter EpubLinkExtractor.Extract(string? html);
  ```
  `Start`/`Offset`/`Length` are indices into `Text`. `Text` MUST equal `EpubDocumentReader.HtmlToText(html)` when the tokenizer succeeds; the reader compares them and drops the chapter's links on any mismatch (so exact parity is a coverage goal, not a correctness risk).

**Approach:** single left-to-right pass over `html`, emitting the same normalization `HtmlToText` performs, while tracking output length and the open `<a href>`/anchor positions. Normalization rules to mirror (same as `HtmlToText`): drop `<script>/<style>` content; `<br>`→`\n`; block tags (the `BlockTagRegex` set)→`\n\n`; other tags→` `; decode entities; drop soft hyphen `­`; collapse spaces-around-newline to `\n`, runs of `[ \t\f\v]`→` `, 3+ newlines→`\n\n`; trim ends. Emit into a `StringBuilder`, then apply the identical post-collapse regexes (`SpacesAroundNewlineRegex`, `InlineWhitespaceRegex`, `ExcessNewlinesRegex`) and `Trim()` used by `HtmlToText`, adjusting recorded positions through the collapses.

To keep parity exact and simple, implement `Extract` as: **(a)** build a "marked" string using the *same* pipeline as `HtmlToText` but on HTML with sentinel scalars inserted at each `<a>` open (``), `</a>` close (``), and anchor point (``); **(b)** run the shared normalization; **(c)** walk the result, stripping sentinels and recording each one's offset in the cleaned text (pairing by order with hrefs/ids captured during insertion). Because whitespace adjacent to a tag can perturb the collapse, the reader validates `Text == HtmlToText(html)` and drops links when they differ — so this stays correct regardless. Extract the shared pipeline into a private `Normalize(string)` used by both `HtmlToText` and `Extract` (Task 2 moves `HtmlToText` to call it; for Task 1, duplicate the regex steps in the extractor and reconcile in Task 2).

- [ ] **Step 1: Write failing tests**

```csharp
using BabelRead.Core.Documents;
using Xunit;

namespace BabelRead.Core.Tests.Documents;

public class EpubLinkExtractorTests
{
    [Fact]
    public void Captures_an_inline_link_span_and_its_href()
    {
        var html = "<p>See <a href=\"ch2.xhtml#n1\">Chapter 2</a> now.</p>";
        var r = EpubLinkExtractor.Extract(html);

        var link = Assert.Single(r.Links);
        Assert.Equal("ch2.xhtml#n1", link.Href);
        Assert.Equal("Chapter 2", r.Text.Substring(link.Start, link.Length));
    }

    [Fact]
    public void Captures_an_anchor_id_at_its_text_position()
    {
        var html = "<p>Intro.</p><h2 id=\"n1\">Notes</h2><p>Body.</p>";
        var r = EpubLinkExtractor.Extract(html);

        var anchor = Assert.Single(r.Anchors, a => a.Id == "n1");
        Assert.StartsWith("Notes", r.Text[anchor.Offset..]);
    }

    [Fact]
    public void Captures_an_a_name_anchor()
    {
        var r = EpubLinkExtractor.Extract("<p><a name=\"top\"></a>Start here.</p>");
        Assert.Contains(r.Anchors, a => a.Id == "top");
    }

    [Fact]
    public void Empty_or_null_html_yields_empty_result()
    {
        var r = EpubLinkExtractor.Extract(null);
        Assert.Equal(string.Empty, r.Text);
        Assert.Empty(r.Links);
        Assert.Empty(r.Anchors);
    }
}
```

Run: `dotnet test tests/BabelRead.Core.Tests/BabelRead.Core.Tests.csproj --filter "FullyQualifiedName~EpubLinkExtractor"` → FAIL (type missing).

- [ ] **Step 2: Implement `EpubLinkExtractor`**

```csharp
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BabelRead.Core.Documents;

public readonly record struct RawLinkSpan(int Start, int Length, string Href);
public readonly record struct RawAnchor(string Id, int Offset);
public readonly record struct ExtractedChapter(
    string Text, IReadOnlyList<RawLinkSpan> Links, IReadOnlyList<RawAnchor> Anchors);

/// <summary>Recovers link spans and anchor positions from EPUB chapter HTML by threading private-use
/// sentinels through the same normalization the reader uses for text. The caller must treat the result
/// as trustworthy only when <see cref="ExtractedChapter.Text"/> equals the reader's own text output.</summary>
public static partial class EpubLinkExtractor
{
    private const char LinkOpen = '';
    private const char LinkClose = '';
    private const char AnchorMark = '';

    public static ExtractedChapter Extract(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return new ExtractedChapter(string.Empty, [], []);
        }

        // 1. Locate <a href>, </a>, and id/name anchors; splice sentinels in, remembering hrefs/ids in order.
        var hrefs = new List<string>();
        var ids = new List<string>();
        var marked = InsertSentinels(html, hrefs, ids);

        // 2. Normalize exactly as the reader does (sentinels are PUA scalars: untouched by every step).
        var normalized = EpubDocumentReader.NormalizeHtml(marked);

        // 3. Strip sentinels, recording their offsets in the clean text; pair by appearance order.
        var text = new StringBuilder(normalized.Length);
        var links = new List<RawLinkSpan>();
        var anchors = new List<RawAnchor>();
        var openStack = new Stack<(int Start, string Href)>(); // unmatched link opens (handles nesting, LIFO)
        var hrefQueue = new Queue<string>(hrefs);              // hrefs in open order
        var idQueue = new Queue<string>(ids);                 // ids in anchor order
        foreach (var ch in normalized)
        {
            switch (ch)
            {
                case LinkOpen:
                    openStack.Push((text.Length, hrefQueue.Count > 0 ? hrefQueue.Dequeue() : string.Empty));
                    break;
                case LinkClose:
                    if (openStack.Count > 0)
                    {
                        var (start, href) = openStack.Pop();
                        links.Add(new RawLinkSpan(start, text.Length - start, href));
                    }
                    break;
                case AnchorMark:
                    var id = idQueue.Count > 0 ? idQueue.Dequeue() : string.Empty;
                    if (id.Length > 0)
                    {
                        anchors.Add(new RawAnchor(id, text.Length));
                    }
                    break;
                default:
                    text.Append(ch);
                    break;
            }
        }

        return new ExtractedChapter(text.ToString(), links, anchors);
    }

    /// <summary>Splice sentinels into the raw HTML: <c>LinkClose</c> just before each <c>&lt;/a&gt;</c>;
    /// after every other tag, <c>AnchorMark</c> when it carries id/name (recording the id) and
    /// <c>LinkOpen</c> when it is an &lt;a&gt; with an href (recording the href).</summary>
    private static string InsertSentinels(string html, List<string> hrefs, List<string> ids)
    {
        var sb = new StringBuilder(html.Length + 16);
        var pos = 0;
        foreach (Match m in TagRegex().Matches(html)) // TagRegex = <[^>]+>
        {
            sb.Append(html, pos, m.Index - pos);
            pos = m.Index + m.Length;
            var tag = m.Value;

            if (CloseAnchorRegex().IsMatch(tag))
            {
                sb.Append(LinkClose).Append(tag); // close sentinel sits before </a>, at the link text end
                continue;
            }

            sb.Append(tag); // the opening/other tag itself
            var id = AttrRegex("id").Match(tag) is { Success: true } idm ? WebUtility.HtmlDecode(idm.Groups[1].Value) : null;
            if (id is null && IsAnchorRegex().IsMatch(tag)
                && AttrRegex("name").Match(tag) is { Success: true } nm)
            {
                id = WebUtility.HtmlDecode(nm.Groups[1].Value); // <a name="..."> is a legacy anchor
            }
            if (id is { Length: > 0 })
            {
                ids.Add(id);
                sb.Append(AnchorMark);
            }

            if (IsAnchorRegex().IsMatch(tag) && AttrRegex("href").Match(tag) is { Success: true } hm)
            {
                hrefs.Add(WebUtility.HtmlDecode(hm.Groups[1].Value));
                sb.Append(LinkOpen);
            }
        }

        sb.Append(html, pos, html.Length - pos);
        return sb.ToString();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("^</a\\s*>$", RegexOptions.IgnoreCase)]
    private static partial Regex CloseAnchorRegex();

    [GeneratedRegex("^<a\\b", RegexOptions.IgnoreCase)]
    private static partial Regex IsAnchorRegex();

    // Attribute value (double- or single-quoted) for a named attribute; group[1] is the value.
    private static Regex AttrRegex(string name) => new(
        $"\\b{name}\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase);
}
```

> Implementer note: `AttrRegex` returns group 1 for double-quoted and group 2 for single-quoted; read
> `m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value`. Prefer caching the two attribute regexes
> (`id`/`name`/`href`) in static fields rather than reconstructing per call. A tag that is both an `<a href>`
> and carries an `id` emits both an `AnchorMark` and a `LinkOpen` (anchor first), which the strip loop's
> independent queues handle. `WebUtility.HtmlDecode` on attribute values matches how the pipeline decodes text.

- [ ] **Step 3: Add `NormalizeHtml` seam (temporary) to `EpubDocumentReader`**

Add an `internal static string NormalizeHtml(string marked)` that runs the exact pipeline currently inside `HtmlToText` (script/style removal, br, block, tag, decode, soft-hyphen, the three collapses, trim). Have `HtmlToText` call `NormalizeHtml`. This is the shared normalization the extractor reuses. (Full move/reconcile is Task 2; the method must exist for Task 1 to compile.)

- [ ] **Step 4: Run tests → PASS**

Run: `dotnet test tests/BabelRead.Core.Tests/BabelRead.Core.Tests.csproj --filter "FullyQualifiedName~EpubLinkExtractor"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BabelRead.Core/Documents/EpubLinkExtractor.cs src/BabelRead.Core/Documents/EpubDocumentReader.cs tests/BabelRead.Core.Tests/Documents/EpubLinkExtractorTests.cs
git commit -m "Add EPUB link/anchor extractor with sentinel position tracking"
```

---

## Task 2: Map links into the document model

**Files:**
- Create: `src/BabelRead.Core/Domain/DocumentLink.cs`
- Modify: `src/BabelRead.Core/Domain/Document.cs`
- Modify: `src/BabelRead.Core/Documents/EpubDocumentReader.cs`
- Test: `tests/BabelRead.Core.Tests/Documents/EpubDocumentReaderTests.cs`

**Interfaces:**
- Consumes: `EpubLinkExtractor.Extract`, `EpubDocumentReader.HtmlToText`.
- Produces:
  ```csharp
  public sealed record DocumentLink(int SegmentIndex, int Start, int Length, string TargetKey);
  public readonly record struct LinkTarget(int SegmentIndex, int Offset);
  // Document:
  public IReadOnlyList<DocumentLink> Links { get; }
  public IReadOnlyDictionary<string, LinkTarget> Anchors { get; }
  ```

**Design:**
- `Document` gains two optional ctor params (default empty), so PDF and existing callers are unaffected.
- In `BuildChapterSegments`, for each spine item: compute `text = HtmlToText(chapter.Content)` (unchanged — this is what is segmented) **and** `extracted = EpubLinkExtractor.Extract(chapter.Content)`. Use `extracted.Links`/`Anchors` only when `extracted.Text == text`; otherwise skip this chapter's links (text is still `text`).
- Track per-segment ranges: refactor `SplitIntoSegments` to also return each emitted segment's `[start,len)` in `text` (a parallel `SplitIntoSegmentsWithRanges` returning `IReadOnlyList<(string Text, int Start, int Length)>`; keep `SplitIntoSegments` delegating to it). Map a chapter-text offset to `(localSegmentIndex, offsetInSegment)` by finding the range that contains it (clamp to nearest on gaps from trimming).
- Global `segmentIndex` = running count of segments across prior chapters + local index.
- Resolve hrefs against `chapter.FilePath`:
  - split off `#fragment`; url-decode the path and fragment.
  - path empty → same file → key `{normalize(chapter.FilePath)}#{frag}`.
  - path present → resolve relative to the directory of `chapter.FilePath` (handle `../`, subdirs) → `{normalizedResolvedPath}#{frag}` (or without `#` when no fragment).
  - scheme `http/https/mailto/tel/data` or protocol-relative `//` → external → drop.
  - Normalize paths with `NormalizePath` (collapse `.`/`..`, forward slashes, case-sensitive as stored).
- Anchors: for each `RawAnchor`, key `{normalize(chapter.FilePath)}#{id}` → `LinkTarget(globalSegmentIndex, offsetInSegment)`. Also register a bare `{normalize(chapter.FilePath)}` → first segment of the chapter (offset 0) for whole-file links.
- Emit a `DocumentLink` only when its `TargetKey` exists in the anchors map; otherwise drop (broken internal ref → plain text). Build anchors first (all chapters), then filter links.

- [ ] **Step 1: Write failing tests** (in `EpubDocumentReaderTests.cs`)

```csharp
[Fact]
public async Task Internal_link_resolves_to_the_target_segment()
{
    // SampleDocuments.CreateEpubWithLink builds a 2-chapter book: ch1 links to an id in ch2.
    var path = SampleDocuments.CreateEpubWithInternalLink(Path.Combine(_dir, "linked.epub"));
    var reader = new EpubDocumentReader();
    var doc = await reader.OpenAsync(path, TestContext.Current.CancellationToken);

    var link = Assert.Single(doc.Links);
    Assert.True(doc.Anchors.ContainsKey(link.TargetKey));
    var target = doc.Anchors[link.TargetKey];
    Assert.True(target.SegmentIndex > link.SegmentIndex); // points forward into chapter 2
}

[Fact]
public async Task External_and_dangling_links_are_dropped()
{
    var path = SampleDocuments.CreateEpubWithExternalAndDanglingLinks(Path.Combine(_dir, "ext.epub"));
    var reader = new EpubDocumentReader();
    var doc = await reader.OpenAsync(path, TestContext.Current.CancellationToken);

    Assert.Empty(doc.Links); // http link + link to a missing id both dropped
}

[Fact]
public async Task Segment_text_is_unchanged_by_link_extraction()
{
    // A plain book with no links must produce exactly the same segments as before.
    var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "plain.epub"), "Book", "en",
        "<p>Chapter one paragraph.</p>", "<p>Chapter two paragraph.</p>");
    var reader = new EpubDocumentReader();
    var doc = await reader.OpenAsync(path, TestContext.Current.CancellationToken);

    Assert.Contains(doc.Segments, s => s.Contains("Chapter one", StringComparison.Ordinal));
    Assert.Empty(doc.Links);
}
```

Add to `SampleDocuments`:
```csharp
public static string CreateEpubWithInternalLink(string path) => CreateEpub(path, "Linked", "en",
    "<p>See <a href=\"ch2.xhtml#note\">the note</a>.</p>",
    "<p id=\"note\">The note body is here with enough words to form a segment.</p>");

public static string CreateEpubWithExternalAndDanglingLinks(string path) => CreateEpub(path, "Ext", "en",
    "<p><a href=\"https://example.com\">out</a> and <a href=\"#missing\">nowhere</a>.</p>");
```
> Implementer note: `CreateEpub` must name spine files predictably (`ch1.xhtml`, `ch2.xhtml`, ...) so hrefs resolve. If the current `CreateEpub` does not control spine file names, extend it (or add an overload) so the second chapter's file is `ch2.xhtml`. Verify against VersOne.Epub's `ReadingOrder[i].FilePath`.

Run → FAIL.

- [ ] **Step 2: Add domain types** (`DocumentLink.cs`) and `Links`/`Anchors` on `Document` (optional ctor params defaulting to `[]` / empty dictionary).

- [ ] **Step 3: Implement mapping in `EpubDocumentReader`** per the Design above (`SplitIntoSegmentsWithRanges`, href resolution `ResolveHref`, `NormalizePath`, anchor table build, link filter). Populate `Document.Links`/`Anchors` in `OpenAsync`. PDF/`Document` default paths unaffected.

- [ ] **Step 4: Run tests → PASS** (both new tests and the whole `BabelRead.Core.Tests` suite, to confirm segment stability): `dotnet test tests/BabelRead.Core.Tests/BabelRead.Core.Tests.csproj`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Resolve EPUB internal links to target segments on the document model"
```

---

## Task 3: View-model — follow links and Back

**Files:**
- Modify: `src/BabelRead.App/ViewModels/ReaderViewModel.cs`
- Test: `tests/BabelRead.App.Tests/ReaderViewModelTests.cs`

**Interfaces:**
- Consumes: `Document.Links`, `Document.Anchors`, existing `_segmentCharOffsets`, `_pageStartOffset`, `RemapOffset`, `OnVisualPageChangedAsync`, `PageStartCharOffset`.
- Produces (public surface used by the view):
  ```csharp
  public IReadOnlyList<VisibleLink> VisibleLinks { get; }        // links inside the current slice, slice-relative
  public readonly record struct VisibleLink(int Start, int Length, string TargetKey);
  public Task FollowLinkAsync(string targetKey);
  public bool CanGoBackFromLink { get; }                          // ObservableProperty
  public Task GoBackFromLinkAsync();                              // RelayCommand
  ```

**Design:**
- Store `_links` (from `Document.Links`) and `_anchors` (from `Document.Anchors`) on open (empty for PDF).
- `_linkReturnStack` = `Stack<int>` of flow offsets; `CanGoBackFromLink = _linkReturnStack.Count > 0`.
- `VisibleLinks`: computed in a private `RebuildVisibleLinks()` called from `ReSlice`/after navigation. Empty when `ShowingTranslation` is true or metrics null. Otherwise, for each `DocumentLink`, its flow range is `_segmentCharOffsets[SegmentIndex] + Start`, length `Length`; intersect with `[_pageStartOffset, _pageStartOffset + consumed)`; if it overlaps, add `VisibleLink(flowStart - _pageStartOffset (clamped ≥0), overlapLength, TargetKey)`. Expose as an `ObservableProperty` list so the view rebinds. (`consumed` = `VisiblePageText.Length`.)
- `FollowLinkAsync(targetKey)`:
  ```csharp
  public async Task FollowLinkAsync(string targetKey)
  {
      if (_document is null || !_anchors.TryGetValue(targetKey, out var target)) return;
      var destination = SegmentStartOffset(target.SegmentIndex) + target.Offset; // flow offset (original view)
      _linkReturnStack.Push(_pageStartOffset);
      CanGoBackFromLink = true;
      _visitedPageStarts.Clear();                 // a jump is a discontinuity for the page back-stack
      _pageStartOffset = Math.Clamp(destination, 0, Math.Max(0, (DisplayText?.Length ?? 1) - 1));
      await OnVisualPageChangedAsync(ReadingDirection.Forward).ConfigureAwait(true);
  }
  ```
  where `SegmentStartOffset(i)` returns `_segmentCharOffsets[clamp i]`.
- `GoBackFromLinkAsync` (RelayCommand):
  ```csharp
  [RelayCommand]
  public async Task GoBackFromLinkAsync()
  {
      if (_linkReturnStack.Count == 0) return;
      _pageStartOffset = Math.Clamp(_linkReturnStack.Pop(), 0, Math.Max(0, (DisplayText?.Length ?? 1) - 1));
      CanGoBackFromLink = _linkReturnStack.Count > 0;
      _visitedPageStarts.Clear();
      await OnVisualPageChangedAsync(ReadingDirection.Backward).ConfigureAwait(true);
  }
  ```
- In `BuildContinuousText`'s remap block, also remap every `_linkReturnStack` entry with `RemapOffset` (same pattern as `_visitedPageStarts`).
- Call `RebuildVisibleLinks()` at the end of `ReSlice` and in `OnShowingTranslationChanged` (so toggling clears/repopulates links).

- [ ] **Step 1: Write failing tests**

```csharp
[AvaloniaFact]
public async Task Following_an_internal_link_jumps_to_the_target_and_Back_returns()
{
    var vm = CreateViewModel();
    await vm.OpenAsync(SampleDocuments.CreateEpubWithInternalLink(Path.Combine(_dir, "linked.epub")));
    SetMetrics(vm);
    vm.ShowingTranslation = false;               // links live in the original view

    var start = vm.VisiblePageText;
    var link = Assert.Single(vm.VisibleLinks);   // the ch1 link is on the first page

    await vm.FollowLinkAsync(link.TargetKey);
    Assert.True(vm.CanGoBackFromLink);
    Assert.Contains("note body", vm.VisiblePageText!, StringComparison.OrdinalIgnoreCase);

    await vm.GoBackFromLinkAsync();
    Assert.False(vm.CanGoBackFromLink);
    Assert.Equal(start, vm.VisiblePageText);     // returned to the exact spot
}

[AvaloniaFact]
public async Task Links_are_not_exposed_while_reading_the_translation()
{
    var vm = CreateViewModel();
    await vm.OpenAsync(SampleDocuments.CreateEpubWithInternalLink(Path.Combine(_dir, "linked.epub")));
    SetMetrics(vm);

    vm.ShowingTranslation = true;
    Assert.Empty(vm.VisibleLinks);
    vm.ShowingTranslation = false;
    Assert.NotEmpty(vm.VisibleLinks);
}
```

Run → FAIL.

- [ ] **Step 2: Implement** the fields, `VisibleLink`/`VisibleLinks`, `RebuildVisibleLinks`, `FollowLinkAsync`, `GoBackFromLink` command + `CanGoBackFromLink`, return-stack remap. Store `_links`/`_anchors` in the open path (where `_document` is assigned).

- [ ] **Step 3: Run tests → PASS** (`--filter "FullyQualifiedName~Following_an_internal_link|FullyQualifiedName~Links_are_not_exposed"`).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Follow internal links and browser-style Back in the reader view-model"
```

---

## Task 4: View — clickable link runs and Back control

**Files:**
- Create: `src/BabelRead.App/Controls/LinkableTextBlock.cs`
- Modify: `src/BabelRead.App/Views/ReaderView.axaml`, `src/BabelRead.App/Views/ReaderView.axaml.cs`
- Test: `tests/BabelRead.App.Tests/ViewSmokeTests.cs`

**Interfaces:**
- Consumes: `ReaderViewModel.VisiblePageText`, `VisibleLinks`, `ShowingTranslation`, `FollowLinkAsync`, `GoBackFromLinkCommand`, `CanGoBackFromLink`.

**`LinkableTextBlock : SelectableTextBlock`:**
- `StyledProperty<IReadOnlyList<VisibleLink>> Links` and `StyledProperty<bool> LinksEnabled`. On either changing (or `Text` changing), rebuild `Inlines`: a plain `Run` for each gap and, for each link, a `Run` with `Foreground = accent` and `TextDecorations = Underline`. When `LinksEnabled` is false (translation view) or `Links` empty, set `Text` and clear inlines (plain path).
- `event EventHandler<string>? LinkInvoked;`
- Override `OnPointerReleased`: if `LinksEnabled` and the gesture was a click (not a drag-select — compare press/release positions within a few px, or check `SelectedText` is empty), access the protected `TextLayout`, call `TextLayout.HitTestPoint(e.GetPosition(this))`, and if `result.IsInside`, map `result.TextPosition` to a link whose `[Start, Start+Length)` contains it → raise `LinkInvoked(this, targetKey)`.

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using BabelRead.App.ViewModels;

namespace BabelRead.App.Controls;

public sealed class LinkableTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<IReadOnlyList<VisibleLink>?> LinksProperty =
        AvaloniaProperty.Register<LinkableTextBlock, IReadOnlyList<VisibleLink>?>(nameof(Links));
    public static readonly StyledProperty<bool> LinksEnabledProperty =
        AvaloniaProperty.Register<LinkableTextBlock, bool>(nameof(LinksEnabled));

    public IReadOnlyList<VisibleLink>? Links { get => GetValue(LinksProperty); set => SetValue(LinksProperty, value); }
    public bool LinksEnabled { get => GetValue(LinksEnabledProperty); set => SetValue(LinksEnabledProperty, value); }

    public event EventHandler<string>? LinkInvoked;

    private Point _pressPoint;

    static LinkableTextBlock()
    {
        // Rebuild inlines when text or the link set changes.
        TextProperty.Changed.AddClassHandler<LinkableTextBlock>((c, _) => c.Rebuild());
        LinksProperty.Changed.AddClassHandler<LinkableTextBlock>((c, _) => c.Rebuild());
        LinksEnabledProperty.Changed.AddClassHandler<LinkableTextBlock>((c, _) => c.Rebuild());
    }

    private void Rebuild()
    {
        var text = Text ?? string.Empty;
        var links = LinksEnabled ? Links : null;
        if (links is null || links.Count == 0)
        {
            Inlines?.Clear();
            SetCurrentValue(TextProperty, text); // plain path
            return;
        }

        var accent = this.FindResource("SystemAccentColor") is Color c ? new SolidColorBrush(c) : Brushes.SteelBlue;
        var inlines = new InlineCollection();
        var cursor = 0;
        foreach (var link in links.OrderBy(l => l.Start))
        {
            var start = Math.Clamp(link.Start, 0, text.Length);
            var end = Math.Clamp(start + link.Length, start, text.Length);
            if (start > cursor) inlines.Add(new Run(text[cursor..start]));
            inlines.Add(new Run(text[start..end])
            {
                Foreground = accent,
                TextDecorations = TextDecorations.Underline,
            });
            cursor = end;
        }
        if (cursor < text.Length) inlines.Add(new Run(text[cursor..]));
        Inlines!.Clear();
        Inlines.AddRange(inlines);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!LinksEnabled || Links is not { Count: > 0 } links) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPoint.X) > 3 || Math.Abs(p.Y - _pressPoint.Y) > 3) return; // a drag/selection
        if (!string.IsNullOrEmpty(SelectedText)) return;

        var hit = TextLayout.HitTestPoint(p);
        if (!hit.IsInside) return;
        foreach (var link in links)
        {
            if (hit.TextPosition >= link.Start && hit.TextPosition < link.Start + link.Length)
            {
                LinkInvoked?.Invoke(this, link.TargetKey);
                e.Handled = true;
                return;
            }
        }
    }
}
```
> Implementer note: confirm `SelectableTextBlock.TextLayout` is accessible (protected) and `TextLayout.HitTestPoint(Point)` returns a `TextHitTestResult` with `IsInside` and `TextPosition` in Avalonia 12.1.0; adjust member names if the ref assembly differs. If `FindResource("SystemAccentColor")` is not available at build time, fall back to a `DynamicResource`-driven brush property or `Brushes.SteelBlue`.

**AXAML:** replace the single `PageText` `SelectableTextBlock` with the `LinkableTextBlock` (xmlns `controls:`), binding `Text="{Binding VisiblePageText}"`, `Links="{Binding VisibleLinks}"`, `LinksEnabled="{Binding !ShowingTranslation}"`, keeping the existing `FlowDirection`, wrapping, alignment, margins, font bindings. Add a Back control to the toolbar nav group: a `‹` button `Command="{Binding GoBackFromLinkCommand}"` `IsVisible="{Binding CanGoBackFromLink}"` `AutomationProperties.Name="Back"`.

**Code-behind:** wire `PageText.LinkInvoked += (_, key) => ViewModel?.FollowLinkAsync(key);` (fire-and-forget on the UI thread). In `OnReaderKeyDown`, handle `Key.Back` and `Alt+Left` → `await ViewModel.GoBackFromLinkAsync();` (before the plain `Left` paging case, and only when `CanGoBackFromLink`, so Left still pages otherwise).

- [ ] **Step 1: Write a failing smoke test**

```csharp
[AvaloniaFact]
public void Original_view_renders_a_clickable_link_run_translation_view_does_not()
{
    var store = new InMemoryTranslationStore();
    var vm = new ReaderViewModel(
        new DocumentReaderRegistry(new IDocumentReader[] { new EpubDocumentReader() }),
        new TranslationService(new StubChatClientFactory(new FakeChatClient()), store),
        store, new NoOpPrefetchCoordinator(),
        new JsonPreferencesStore(Path.Combine(Path.GetTempPath(), $"{System.Guid.NewGuid():n}.json")));
    vm.ShowingTranslation = false;
    vm.VisiblePageText = "See the note here.";
    // Simulate one visible link over "note".
    vm.SetVisibleLinksForTest(new[] { new VisibleLink(8, 4, "ch2.xhtml#note") });

    var view = new ReaderView { DataContext = vm };
    var window = new Window { Content = view, Width = 800, Height = 900 };
    window.Show();
    Dispatcher.UIThread.RunJobs();

    var block = view.FindControl<Avalonia.Controls.Documents.Inline>("PageText"); // via LinkableTextBlock
    var page = view.GetVisualDescendants().OfType<BabelRead.App.Controls.LinkableTextBlock>().Single();
    Assert.Contains(page.Inlines!, i => i is Run r && r.TextDecorations == TextDecorations.Underline);

    page.LinksEnabled = false; // translation view
    Dispatcher.UIThread.RunJobs();
    Assert.DoesNotContain(page.Inlines!, i => i is Run r && r.TextDecorations == TextDecorations.Underline);
}
```
> Implementer note: expose a tiny test seam `internal void SetVisibleLinksForTest(IReadOnlyList<VisibleLink>)` on the VM (or make `VisibleLinks` settable internally) so the smoke test can drive the control without a full EPUB open. Prefer `[InternalsVisibleTo]` already used by the test project; if not present, drive via a real `CreateEpubWithInternalLink` open + `SetMetrics` instead.

Run → FAIL.

- [ ] **Step 2: Implement** `LinkableTextBlock`, swap the AXAML control, wire code-behind (`LinkInvoked`, Back keys), add the Back button.

- [ ] **Step 3: Run the App view tests → PASS** and the full App suite once (`dotnet test tests/BabelRead.App.Tests/BabelRead.App.Tests.csproj`); expect it slow (~4–5 min).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Render clickable internal links in the original view with browser-style Back"
```

---

## Self-review checklist (run before final review)

- Segment text identical: `BabelRead.Core.Tests` all green, including a no-link book producing the same segments.
- `dotnet build` 0 warnings / 0 errors.
- External/dangling links dropped; internal links resolve; PDF unaffected (empty `Links`/`Anchors`).
- Follow pushes the return stack; Back returns to the exact prior offset; return-stack offsets remapped on toggle/landing translation.
- Links present in original view only; translation view unchanged.
- Full suites: App, Core, Integration all green.
