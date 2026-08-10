using BabelRead.Core.Documents;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Core.Tests.Documents;

public class EpubContentsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-contents").FullName;

    [Fact]
    public async Task Every_navigation_entry_becomes_a_contents_entry_that_resolves()
    {
        var path = SampleDocuments.CreateEpub(Path.Combine(_dir, "flat.epub"), "Book", "en",
            "<p>Chapter one paragraph.</p>", "<p>Chapter two paragraph.</p>");
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Equal(["Chapter 1", "Chapter 2"], doc.Contents.Select(c => c.Title));
        Assert.All(doc.Contents, c => Assert.True(doc.Anchors.ContainsKey(c.TargetKey), $"'{c.Title}' must resolve"));
        Assert.All(doc.Contents, c => Assert.Equal(0, c.Depth));
    }

    [Fact]
    public async Task Nested_navigation_keeps_each_entrys_depth_and_reading_order()
    {
        var path = SampleDocuments.CreateEpubWithNestedContents(Path.Combine(_dir, "nested.epub"));
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Equal(["Leaders", "Britain", "Culture"], doc.Contents.Select(c => c.Title));
        Assert.Equal([0, 1, 1], doc.Contents.Select(c => c.Depth));

        // Reading order: each entry lands on a later segment than the one before it.
        var segments = doc.Contents.Select(c => doc.Anchors[c.TargetKey].SegmentIndex).ToArray();
        Assert.Equal(segments.OrderBy(s => s), segments);
    }

    [Fact]
    public async Task An_entry_naming_a_fragment_the_chapter_lost_still_lands_on_that_chapter()
    {
        // Hand-maintained navigation outlives the ids it points at. Landing at the top of the right chapter
        // is far better than dropping the entry, so the fragment is given up on, not the destination.
        var path = SampleDocuments.CreateEpubWithNavigation(Path.Combine(_dir, "stale.epub"), "Book", "en",
            "<li><a href=\"ch0.xhtml#long-gone\">Chapter 1</a></li>",
            "<p>Chapter one paragraph.</p>");
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        var entry = Assert.Single(doc.Contents);
        Assert.DoesNotContain('#', entry.TargetKey);
        Assert.True(doc.Anchors.ContainsKey(entry.TargetKey));
    }

    [Fact]
    public async Task A_group_label_with_no_destination_is_left_out_but_its_children_stay()
    {
        // EPUB 3 navigation may head a group with a plain <span>: a label, not a place to go.
        var path = SampleDocuments.CreateEpubWithNavigation(Path.Combine(_dir, "label.epub"), "Book", "en",
            "<li><span>Part One</span><ol><li><a href=\"ch0.xhtml\">Chapter 1</a></li></ol></li>",
            "<p>Chapter one paragraph.</p>");
        using var reader = new EpubDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Equal("Chapter 1", Assert.Single(doc.Contents).Title);
    }

    [Fact]
    public async Task A_pdf_offers_no_contents()
    {
        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "doc.pdf"), "Some page text.");
        using var reader = new PdfDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);

        Assert.Empty(doc.Contents);
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
