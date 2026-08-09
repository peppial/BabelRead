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
    public async Task An_anchor_opening_a_chapter_keeps_that_chapters_extraction_usable()
    {
        // ch0's body is an id-only <a> immediately followed by text at a block start. The sentinel the
        // extractor splices in lands inside a whitespace run, which it must re-collapse the way HtmlToText
        // does -- otherwise the texts diverge by a stray space and the compare-and-drop guard throws away
        // every link and anchor in the chapter. Prove the agreement against the exact raw content the reader
        // sees, then that the link downstream of it resolves.
        var path = SampleDocuments.CreateEpubWithLeadingAnchor(Path.Combine(_dir, "leading.epub"));
        var book = await EpubReader.ReadBookAsync(path);
        var chapter0Raw = book.ReadingOrder[0].Content;
        var cleanChapter0Text = EpubDocumentReader.HtmlToText(chapter0Raw);
        Assert.Equal(cleanChapter0Text, EpubLinkExtractor.Extract(chapter0Raw).Text);

        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        // (a) ch0's #note anchor is published and ch1's link to it resolves.
        var link = Assert.Single(doc.Links);
        Assert.EndsWith("#note", link.TargetKey, StringComparison.Ordinal);
        Assert.True(doc.Anchors.ContainsKey(link.TargetKey));

        // (b) the anchor points at the text written right after it, and that text is untouched by extraction.
        var target = doc.Anchors[link.TargetKey];
        Assert.StartsWith("The note text", doc.Segments[target.SegmentIndex][target.Offset..], StringComparison.Ordinal);
        Assert.Contains(doc.Segments, s => s == cleanChapter0Text);
        Assert.DoesNotContain(doc.Segments, s => s.StartsWith(' '));
    }

    [Fact]
    public async Task A_chapter_using_the_extractors_own_marker_characters_drops_its_links_and_anchors()
    {
        // ch0's text contains U+E000 -- a private-use scalar of the kind icon fonts use, indistinguishable
        // from the marker EpubLinkExtractor splices in. Rather than pair hrefs with spans measured against
        // someone else's character, the extractor refuses the chapter and the guard drops what it produced.
        var path = SampleDocuments.CreateEpubWithPrivateUseCharacter(Path.Combine(_dir, "privateuse.epub"));
        var book = await EpubReader.ReadBookAsync(path);
        var chapter0Raw = book.ReadingOrder[0].Content;
        Assert.Equal(string.Empty, EpubLinkExtractor.Extract(chapter0Raw).Text);

        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        // (a) the refused chapter contributes no anchors, so ch1's link to its #note is dropped as unresolved.
        Assert.DoesNotContain(doc.Anchors.Keys, k => k.EndsWith("#note", StringComparison.Ordinal));
        Assert.Empty(doc.Links);

        // (b) the chapter's segment text is still exactly HtmlToText's output -- reading is never affected.
        Assert.Contains(doc.Segments, s => s == EpubDocumentReader.HtmlToText(chapter0Raw));
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
    public async Task Link_to_an_anchor_between_two_segments_lands_on_the_following_one()
    {
        // ch1 holds two standalone paragraphs (each >= MinSegmentChars, so neither is coalesced) with the
        // anchor written between them. Collapsing the whitespace either side of it puts the anchor on the
        // second paragraph's first character: an anchor before a paragraph points at that paragraph.
        var path = SampleDocuments.CreateEpubWithAnchorBetweenTwoSegments(Path.Combine(_dir, "between.epub"));
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var link = Assert.Single(doc.Links);
        Assert.True(doc.Anchors.ContainsKey(link.TargetKey));
        var target = doc.Anchors[link.TargetKey];

        // Pinning the paragraph before it down too (rather than just the one the anchor resolved to) is what
        // proves there really are two ranges here, not one merged segment the anchor fell inside of.
        Assert.Contains("AlphaMarker", doc.Segments[target.SegmentIndex - 1], StringComparison.Ordinal);
        Assert.Contains("BetaMarker", doc.Segments[target.SegmentIndex], StringComparison.Ordinal);
        Assert.Equal(0, target.Offset);
    }

    [Theory]
    // Offsets inside a range map to it directly; the gap between two ranges (whitespace trimmed away) goes
    // to whichever edge is nearer; anything past the last range clamps to its end.
    [InlineData(0, 0, 0)]
    [InlineData(3, 0, 3)]
    [InlineData(11, 1, 1)]
    [InlineData(6, 0, 5)]  // gap 5..10, nearer the first range's end
    [InlineData(9, 1, 0)]  // same gap, nearer the second range's start
    [InlineData(99, 1, 5)] // past every range: clamps to the last one's end
    public void MapOffsetToSegment_places_an_offset_on_a_segment(int offset, int expectedIndex, int expectedOffset)
    {
        // Two ranges with a five-character gap between them, as a chapter's trimmed-away whitespace leaves.
        (string Text, int Start, int Length)[] ranges = [("first", 0, 5), ("later", 10, 5)];

        Assert.Equal((expectedIndex, expectedOffset), EpubDocumentReader.MapOffsetToSegment(ranges, offset));
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
