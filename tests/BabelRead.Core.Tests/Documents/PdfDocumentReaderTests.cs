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
    public async Task First_line_indents_split_a_page_into_paragraphs()
    {
        // Two paragraphs, each with an indented first line (left 80) and body lines at the margin (left 60).
        var path = SampleDocuments.CreatePdfWithLines(Path.Combine(_dir, "indent.pdf"), new[]
        {
            ("Alpha beginning of the first", 80.0, 780.0),
            ("first paragraph continues here", 60.0, 762.0),
            ("and ends on this line.", 60.0, 744.0),
            ("Beta beginning of the second", 80.0, 726.0),
            ("second paragraph body text.", 60.0, 708.0),
        });
        using var reader = new PdfDocumentReader();

        var doc = await reader.OpenAsync(path, CancellationToken.None);
        var page = await reader.GetPageAsync(doc, 0, CancellationToken.None);

        Assert.Equal(2, page.Segments.Count);
        Assert.StartsWith("Alpha", page.Segments[0], StringComparison.Ordinal);
        Assert.Contains("ends on this line", page.Segments[0], StringComparison.Ordinal); // wrapped lines joined
        Assert.StartsWith("Beta", page.Segments[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_large_vertical_gap_splits_paragraphs_even_without_indents()
    {
        // No indents — paragraphs separated only by blank-line spacing (32pt gap vs 18pt line gap).
        var path = SampleDocuments.CreatePdfWithLines(Path.Combine(_dir, "gap.pdf"), new[]
        {
            ("First paragraph line one", 60.0, 780.0),
            ("first paragraph line two", 60.0, 762.0),
            ("first paragraph line three", 60.0, 744.0),
            ("Second paragraph after a gap", 60.0, 712.0),
            ("second paragraph line two", 60.0, 694.0),
        });
        using var reader = new PdfDocumentReader();

        var doc = await reader.OpenAsync(path, CancellationToken.None);
        var page = await reader.GetPageAsync(doc, 0, CancellationToken.None);

        Assert.Equal(2, page.Segments.Count);
        Assert.Contains("line two", page.Segments[0], StringComparison.Ordinal);
        Assert.StartsWith("Second", page.Segments[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tall_pdf_page_reflows_into_more_pages_on_a_smaller_viewport()
    {
        // One physical page with many paragraphs (indented first line + a body line each), far more text
        // than fits a small screen.
        var lines = new List<(string, double, double)>();
        for (var i = 0; i < 30; i++)
        {
            var y = 800.0 - (i * 30);
            lines.Add(($"Paragraph {i} begins here with an indented first line and some words,", 80.0, y));
            lines.Add(("and finishes on a second line at the body margin below it.", 60.0, y - 15));
        }

        var path = SampleDocuments.CreatePdfWithLines(Path.Combine(_dir, "tall.pdf"), lines);
        using var reader = new PdfDocumentReader();

        var wide = await reader.OpenAsync(path, CancellationToken.None);
        reader.UpdateViewport(400, 300); // shrink the reading area
        var narrow = await reader.OpenAsync(path, CancellationToken.None); // reopen picks up the new pagination

        Assert.True(narrow.PageCount > wide.PageCount,
            $"a smaller viewport should split the page into more virtual pages (was {wide.PageCount}, now {narrow.PageCount})");

        // Every paragraph is still present exactly once across the virtual pages — nothing lost or duplicated.
        var reassembled = new List<string>();
        for (var i = 0; i < narrow.PageCount; i++)
        {
            reassembled.AddRange((await reader.GetPageAsync(narrow, i, CancellationToken.None)).Segments);
        }

        Assert.Equal(narrow.Segments, reassembled);
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
        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "c.pdf"), "");
        using var reader = new PdfDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var page = await reader.GetPageAsync(doc, 0, CancellationToken.None);

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
