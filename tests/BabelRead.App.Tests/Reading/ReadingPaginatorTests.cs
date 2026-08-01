using Avalonia.Headless.XUnit;
using Avalonia.Media;
using BabelRead.App.Reading;
using Xunit;

namespace BabelRead.App.Tests.Reading;

public class ReadingPaginatorTests
{
    private static ReadingPageMetrics Metrics(double width = 400, double height = 200, double font = 16) =>
        new(width, height, font, LineHeight: 24, Typeface.Default, FlowDirection.LeftToRight);

    // A long body of text, many sentences, so it wraps to far more than one page.
    private static string LongText()
    {
        var paragraph = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 12));
        return string.Join("\n\n", Enumerable.Repeat(paragraph, 20));
    }

    [AvaloniaFact]
    public void A_page_consumes_some_text_but_not_all_of_a_long_document()
    {
        var paginator = new ReadingPaginator();
        var text = LongText();

        var consumed = paginator.MeasurePage(text, 0, Metrics());

        Assert.True(consumed > 0, "a page must consume some text");
        Assert.True(consumed < text.Length, "a long document must not fit on one page");
    }

    [AvaloniaFact]
    public void Successive_pages_cover_the_whole_document_with_no_loss_or_overlap()
    {
        var paginator = new ReadingPaginator();
        var text = LongText();
        var metrics = Metrics();

        var start = 0;
        var pages = 0;
        while (start < text.Length)
        {
            var consumed = paginator.MeasurePage(text, start, metrics);
            Assert.True(consumed > 0, "every page must make forward progress");
            start += consumed;
            pages++;
            Assert.True(pages < 10_000, "pagination must terminate");
        }

        Assert.Equal(text.Length, start); // exact cover: no loss, no overlap
        Assert.True(pages > 1);
    }

    [AvaloniaFact]
    public void Forward_progress_is_guaranteed_even_for_an_unbreakable_line_wider_than_the_column()
    {
        var paginator = new ReadingPaginator();
        var text = new string('X', 500); // a single token far wider than the column

        var consumed = paginator.MeasurePage(text, 0, Metrics(width: 100, height: 50));

        Assert.True(consumed > 0, "an over-long line must still advance");
    }

    [AvaloniaFact]
    public void Empty_or_exhausted_text_consumes_nothing()
    {
        var paginator = new ReadingPaginator();
        Assert.Equal(0, paginator.MeasurePage("", 0, Metrics()));
        Assert.Equal(0, paginator.MeasurePage("abc", 3, Metrics()));
    }

    [AvaloniaFact]
    public void PageContaining_returns_the_page_whose_range_brackets_the_offset()
    {
        var paginator = new ReadingPaginator();
        var text = LongText();
        var metrics = Metrics();

        // Discover real page boundaries.
        var starts = new List<int> { 0 };
        var cursor = 0;
        while (cursor < text.Length)
        {
            cursor += paginator.MeasurePage(text, cursor, metrics);
            if (cursor < text.Length) starts.Add(cursor);
        }

        Assert.True(starts.Count >= 3, "need several pages for a meaningful test");

        // An offset just inside page 2 must resolve to page index 2 and page 2's start.
        var probe = starts[2] + 1;
        var (pageIndex, pageStart) = paginator.PageContaining(text, probe, metrics);
        Assert.Equal(2, pageIndex);
        Assert.Equal(starts[2], pageStart);

        Assert.Equal(starts.Count, paginator.CountPages(text, metrics));
    }

    [AvaloniaFact]
    public void PageContaining_clamps_offsets_past_the_end_to_the_last_page()
    {
        var paginator = new ReadingPaginator();
        var text = LongText();
        var metrics = Metrics();

        var last = paginator.CountPages(text, metrics) - 1;
        var (pageIndex, _) = paginator.PageContaining(text, text.Length + 999, metrics);
        Assert.Equal(last, pageIndex);
    }
}
