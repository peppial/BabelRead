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
