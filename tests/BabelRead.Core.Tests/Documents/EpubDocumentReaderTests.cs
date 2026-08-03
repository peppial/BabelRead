using BabelRead.Core.Documents;
using BabelRead.TestSupport;
using VersOne.Epub;
using Xunit;

namespace BabelRead.Core.Tests.Documents;

public sealed class EpubDocumentReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-epub").FullName;

    [Fact]
    public void CanOpen_only_epub_files()
    {
        var reader = new EpubDocumentReader();
        Assert.True(reader.CanOpen("/x/book.epub"));
        Assert.False(reader.CanOpen("/x/book.pdf"));
    }

    [Fact]
    public async Task Opens_with_reading_order_pages_and_detected_language()
    {
        var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "a.epub"), "Mon Livre", "fr", "Chapitre un", "Chapitre deux");
        using var reader = new EpubDocumentReader();

        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Equal(2, doc.PageCount);
        Assert.Equal("fr", doc.DetectedSourceLanguage.Code);
        Assert.Equal("Mon Livre", doc.Title);
    }

    [Fact]
    public async Task Opening_the_same_file_preserves_a_stable_document_id()
    {
        var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "stable.epub"), "Stable", "en", "one");
        using var reader = new EpubDocumentReader();

        var first = await reader.OpenAsync(path, CancellationToken.None);
        var second = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Long_chapter_is_split_into_multiple_virtual_pages()
    {
        var longChapter = string.Join(" ", Enumerable.Repeat("This is a long sentence for pagination.", 150));
        var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "long.epub"), "Long", "en", longChapter);
        using var reader = new EpubDocumentReader();

        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.True(doc.PageCount > 1);
        var first = await reader.GetPageAsync(doc, 0, CancellationToken.None);
        Assert.True(first.HasText);
    }

    [Fact]
    public async Task A_table_of_contents_is_coalesced_into_few_segments_not_one_per_line()
    {
        // 200 short entries as separate paragraphs — like a real EPUB table of contents. Each segment is
        // one (paid) translation call, so these must be merged rather than translated one line at a time.
        var entries = Enumerable.Range(1, 200).Select(i => $"Chapter {i}");
        var toc = string.Join("</p><p>", entries); // CreateEpub wraps the body in a single <p>…</p>
        var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "toc.epub"), "TOC", "en", toc);
        using var reader = new EpubDocumentReader();

        var doc = await reader.OpenAsync(path, CancellationToken.None);

        // Segments are the translation units for the whole book; 200 one-line entries must not become
        // 200 of them. (~2000 chars of contents coalesces to a small handful.)
        Assert.True(doc.Segments.Count < 20,
            $"a 200-line contents section should coalesce into a handful of segments, but produced {doc.Segments.Count}");
        var allText = string.Join("\n", doc.Segments);
        Assert.Contains("Chapter 1", allText, StringComparison.Ordinal);
        Assert.Contains("Chapter 200", allText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_paragraphs_are_not_merged_together()
    {
        // Two full-length paragraphs (each well past the merge threshold) must stay as separate segments.
        var p1 = string.Join(" ", Enumerable.Repeat("First paragraph sentence.", 20));
        var p2 = string.Join(" ", Enumerable.Repeat("Second paragraph sentence.", 20));
        var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "prose.epub"), "Prose", "en", $"{p1}</p><p>{p2}");
        using var reader = new EpubDocumentReader();

        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Equal(2, doc.Segments.Count);
    }

    [Fact]
    public async Task Extracts_plain_text_from_html_body()
    {
        var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "b.epub"), "T", "en", "Hello <b>bold</b> world");
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var page = await reader.GetPageAsync(doc, 0, CancellationToken.None);

        Assert.True(page.HasText);
        Assert.Contains("Hello", page.ExtractableText, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", page.ExtractableText, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlToText_strips_tags_and_decodes_entities()
    {
        var text = EpubDocumentReader.HtmlToText("<p>Caf&#233; &amp; th&#233;</p><script>ignore()</script>");
        Assert.Equal("Café & thé", text);
    }

    [Fact]
    public void HtmlToText_preserves_paragraph_and_line_break_structure()
    {
        var html = "<h1>Digital Minimalism</h1><p>Praise<br/>for <i>Digital Minimalism</i></p><p>Second paragraph.</p>";

        var text = EpubDocumentReader.HtmlToText(html);

        Assert.Equal("Digital Minimalism\n\nPraise\nfor Digital Minimalism\n\nSecond paragraph.", text);
    }

    [Fact]
    public async Task Internal_link_resolves_to_the_target_segment()
    {
        // SampleDocuments.CreateEpubWithInternalLink builds a 2-chapter book: ch0 links to an id in ch1.
        var path = SampleDocuments.CreateEpubWithInternalLink(Path.Combine(_dir, "linked.epub"));
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var link = Assert.Single(doc.Links);
        Assert.True(doc.Anchors.ContainsKey(link.TargetKey));
        var target = doc.Anchors[link.TargetKey];
        Assert.True(target.SegmentIndex > link.SegmentIndex); // points forward into chapter 2
    }

    [Fact]
    public async Task External_and_dangling_links_are_dropped()
    {
        var path = SampleDocuments.CreateEpubWithExternalAndDanglingLinks(Path.Combine(_dir, "ext.epub"));
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Empty(doc.Links); // http link + link to a missing id both dropped
    }

    [Fact]
    public async Task Segment_text_is_unchanged_by_link_extraction()
    {
        // A plain book with no links must produce exactly the same segments as before.
        var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "plain.epub"), "Book", "en",
            "<p>Chapter one paragraph.</p>", "<p>Chapter two paragraph.</p>");
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Contains(doc.Segments, s => s.Contains("Chapter one", StringComparison.Ordinal));
        Assert.Empty(doc.Links);
    }

    [Fact]
    public async Task Divergent_chapter_extraction_drops_its_links_and_anchors_but_keeps_clean_segment_text()
    {
        // ch0's body is an id-only <a> immediately followed by text at a block start: the sentinel the
        // extractor splices in splits a whitespace run it can't rejoin, so its Text ends up with a spurious
        // leading space HtmlToText never produces. Prove that divergence directly against the exact raw
        // content the reader sees, before trusting any behavior downstream of the compare-and-drop guard.
        var path = SampleDocuments.CreateEpubWithDivergentAnchor(Path.Combine(_dir, "divergent.epub"));
        var book = await EpubReader.ReadBookAsync(path);
        var chapter0Raw = book.ReadingOrder[0].Content;
        var cleanChapter0Text = EpubDocumentReader.HtmlToText(chapter0Raw);
        Assert.NotEqual(cleanChapter0Text, EpubLinkExtractor.Extract(chapter0Raw).Text);

        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        // (a) the divergent chapter contributes no anchors: its #note fragment must not resolve, so the
        // ch1 link pointing at it is dropped as unresolved rather than corrupted.
        Assert.DoesNotContain(doc.Anchors.Keys, k => k.EndsWith("#note", StringComparison.Ordinal));
        Assert.Empty(doc.Links);

        // (b) the chapter's segment text is still exactly HtmlToText's clean output: uncorrupted, no
        // spurious leading space leaked in from the dropped extraction.
        Assert.Contains(doc.Segments, s => s == cleanChapter0Text);
        Assert.DoesNotContain(doc.Segments, s => s.StartsWith(' '));
    }

    [Fact]
    public async Task Link_to_a_trailing_empty_anchor_resolves_via_the_offset_gap_clamp()
    {
        // ch1 ends with a content-less <a id> anchor right after its only paragraph -- a common EPUB
        // footnote/endnote marker. Its offset lands past the end of every tracked segment range (the
        // whitespace trimmed away between the paragraph and the chapter's end), so resolving it exercises
        // MapOffsetToSegment's edge clamp rather than a plain in-range lookup.
        var path = SampleDocuments.CreateEpubWithTrailingAnchor(Path.Combine(_dir, "trailing.epub"));
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var link = Assert.Single(doc.Links);
        Assert.True(doc.Anchors.ContainsKey(link.TargetKey));
        var target = doc.Anchors[link.TargetKey];

        // Resolves to chapter 1's segment, at a valid position within (or right at the end of) its text --
        // not out of bounds, not silently dropped, not pointing at the wrong chapter.
        var targetSegment = doc.Segments[target.SegmentIndex];
        Assert.InRange(target.Offset, 0, targetSegment.Length);
        Assert.True(target.SegmentIndex > link.SegmentIndex);
    }

    [Fact]
    public async Task Opening_a_non_epub_throws_DocumentOpenException()
    {
        var bogus = Path.Combine(_dir, "bad.epub");
        await File.WriteAllTextAsync(bogus, "not an epub");
        using var reader = new EpubDocumentReader();

        await Assert.ThrowsAsync<DocumentOpenException>(() => reader.OpenAsync(bogus, CancellationToken.None));
    }

    [Fact]
    public async Task Link_to_an_anchor_between_two_segments_resolves_via_the_offset_gap_ternary()
    {
        // ch1 holds two standalone paragraphs (each >= MinSegmentChars, so neither is coalesced) with the
        // anchor sitting on the single-character gap trimmed away between them -- not past every range (the
        // post-loop clamp covered by Link_to_a_trailing_empty_anchor_resolves_via_the_offset_gap_clamp above)
        // but strictly between two tracked ranges. This exercises MapOffsetToSegment's mid-loop two-edge
        // ternary specifically.
        var path = SampleDocuments.CreateEpubWithAnchorBetweenTwoSegments(Path.Combine(_dir, "between.epub"));
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var link = Assert.Single(doc.Links);
        Assert.True(doc.Anchors.ContainsKey(link.TargetKey));
        var target = doc.Anchors[link.TargetKey];

        // The target chapter contributes exactly two segments: the paragraph the anchor is nearer to
        // (AlphaMarker), followed immediately by the other one (BetaMarker). Pinning both down (rather than
        // just the one the anchor resolved to) is what proves there really are two ranges here, not one.
        var firstSegment = doc.Segments[target.SegmentIndex];
        var secondSegment = doc.Segments[target.SegmentIndex + 1];
        Assert.Contains("AlphaMarker", firstSegment, StringComparison.Ordinal);
        Assert.Contains("BetaMarker", secondSegment, StringComparison.Ordinal);

        // The two-edge ternary resolves to the PREVIOUS segment at its end. The post-loop clamp this must
        // be distinguished from would instead return the LAST segment (BetaMarker's) at ITS length -- so
        // landing on the FIRST (AlphaMarker) segment, at its own end, is exactly what proves the mid-loop
        // ternary fired rather than the unconditional clamp after the loop.
        Assert.Equal(firstSegment.Length, target.Offset);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
