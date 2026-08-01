using BabelRead.Core.Documents;
using Xunit;

namespace BabelRead.Core.Tests.Documents;

public sealed class SegmentPaginatorTests
{
    private static string Chars(int n) => new('x', n);

    [Fact]
    public void Packs_a_straddling_segment_onto_the_page_when_that_fills_closer_to_the_budget()
    {
        // Budget 1000. After a 470-char segment, adding a 670-char one overshoots to 1142 (over by 142) —
        // closer to full than stopping at 470 (short by 530) — so it stays, filling the page.
        var pages = SegmentPaginator.Paginate([[Chars(470), Chars(670)]], charsPerPage: 1000);

        Assert.Single(pages);
        Assert.Equal(2, pages[0].Count);
    }

    [Fact]
    public void Breaks_before_a_segment_when_stopping_is_closer_to_the_budget()
    {
        // Budget 1000. After a 900-char segment, adding a 500-char one overshoots to 1402 (over by 402),
        // while stopping at 900 is short by only 100 — so the page ends at the first segment.
        var pages = SegmentPaginator.Paginate([[Chars(900), Chars(500)]], charsPerPage: 1000);

        Assert.Equal(2, pages.Count);
        Assert.Single(pages[0]);
        Assert.Single(pages[1]);
    }

    [Fact]
    public void Does_not_overfill_a_page_past_the_tolerance_even_to_fill_it()
    {
        // Budget 1000. A 200-char segment then a 1200-char one would reach 1402 — past the 1.35x tolerance
        // (1350) — so it breaks rather than build a page that scrolls a long way, even though that leaves
        // the first page short.
        var pages = SegmentPaginator.Paginate([[Chars(200), Chars(1200)]], charsPerPage: 1000);

        Assert.Equal(2, pages.Count);
        Assert.Single(pages[0]);
        Assert.Single(pages[1]);
    }

    [Fact]
    public void Keeps_segments_that_comfortably_fit_together_on_one_page()
    {
        var pages = SegmentPaginator.Paginate([[Chars(300), Chars(300), Chars(300)]], charsPerPage: 1000);

        Assert.Single(pages);
        Assert.Equal(3, pages[0].Count);
    }

    [Fact]
    public void Never_merges_across_sections()
    {
        // Two tiny sections (e.g. EPUB chapters) stay on separate pages even though both would fit one.
        var pages = SegmentPaginator.Paginate([[Chars(100)], [Chars(100)]], charsPerPage: 1000);

        Assert.Equal(2, pages.Count);
    }
}
