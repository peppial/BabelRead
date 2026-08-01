using BabelRead.Core.Documents;
using BabelRead.TestSupport;
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
    public async Task Opening_a_non_epub_throws_DocumentOpenException()
    {
        var bogus = Path.Combine(_dir, "bad.epub");
        await File.WriteAllTextAsync(bogus, "not an epub");
        using var reader = new EpubDocumentReader();

        await Assert.ThrowsAsync<DocumentOpenException>(() => reader.OpenAsync(bogus, CancellationToken.None));
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
