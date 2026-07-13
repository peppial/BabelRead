using BabelRead.Core.Documents;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Core.Tests.Documents;

public sealed class PdfDocumentReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-pdf").FullName;

    [Fact]
    public void CanOpen_only_pdf_files()
    {
        var reader = new PdfDocumentReader();
        Assert.True(reader.CanOpen("/x/book.pdf"));
        Assert.False(reader.CanOpen("/x/book.epub"));
    }

    [Fact]
    public async Task Opens_and_reports_page_count()
    {
        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "a.pdf"), "Hello world", "Second page");
        using var reader = new PdfDocumentReader();

        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public async Task Opening_the_same_file_preserves_a_stable_document_id()
    {
        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "stable.pdf"), "Hello");
        using var reader = new PdfDocumentReader();

        var first = await reader.OpenAsync(path, CancellationToken.None);
        var second = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Extracts_page_text()
    {
        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "b.pdf"), "Bonjour le monde");
        using var reader = new PdfDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var page = await reader.GetPageAsync(doc, 0, CancellationToken.None);

        Assert.True(page.HasText);
        Assert.Contains("Bonjour", page.ExtractableText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_less_page_reports_no_text()
    {
        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "c.pdf"), "Has text", "");
        using var reader = new PdfDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var page = await reader.GetPageAsync(doc, 1, CancellationToken.None);

        Assert.False(page.HasText);
    }

    [Fact]
    public async Task Opening_a_non_pdf_throws_DocumentOpenException()
    {
        var bogus = Path.Combine(_dir, "bad.pdf");
        await File.WriteAllTextAsync(bogus, "this is not a pdf");
        using var reader = new PdfDocumentReader();

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
