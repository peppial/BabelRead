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

    // The extraction below is only usable if it agrees with HtmlToText character for character -- that is
    // what EpubDocumentReader's compare-and-drop guard checks. Every case here asserts the agreement first,
    // because a sentinel sitting inside a whitespace run used to split it and leave whitespace behind.

    [Fact]
    public void A_list_entry_carrying_both_an_id_and_a_link_agrees_with_the_reader_text()
    {
        // The shape every Calibre-built table of contents uses: id on the <li>, link inside it. Between two
        // entries the second <li>'s anchor sentinel sits in the middle of </li><li>'s four newlines, which
        // must still collapse to the one paragraph break HtmlToText produces.
        var html = "<ul><li id=\"feed_0\"><a href=\"feed_0/index.html\">Europe</a></li>"
            + "<li id=\"feed_1\"><a href=\"feed_1/index.html\">Britain</a></li></ul>";
        var r = EpubLinkExtractor.Extract(html);

        Assert.Equal(EpubDocumentReader.HtmlToText(html), r.Text);
        Assert.Equal("Europe\n\nBritain", r.Text);
        Assert.Equal(["Europe", "Britain"], r.Links.Select(l => r.Text.Substring(l.Start, l.Length)));
        var anchor = Assert.Single(r.Anchors, a => a.Id == "feed_1");
        Assert.StartsWith("Britain", r.Text[anchor.Offset..]);
    }

    [Fact]
    public void A_space_between_a_stripped_tag_and_the_link_text_collapses_once()
    {
        // <b> and <a> each leave a space behind, and the source adds another after the <a>: three spaces
        // around one link-open sentinel, all of which HtmlToText collapses into a single one.
        var html = "<p>Wildfires raged in <b>Spain</b> <a href=\"ch2.xhtml\"> as</a> temperatures soared.</p>";
        var r = EpubLinkExtractor.Extract(html);

        Assert.Equal(EpubDocumentReader.HtmlToText(html), r.Text);
        Assert.DoesNotContain("  ", r.Text, StringComparison.Ordinal);
        var link = Assert.Single(r.Links);
        Assert.Equal("as", r.Text.Substring(link.Start, link.Length));
    }

    [Fact]
    public void A_non_breaking_space_next_to_a_sentinel_survives()
    {
        // NBSP is whitespace to string.Trim but not to the reader's collapse regexes, so it must be left
        // exactly where it is rather than folded into the run beside the sentinel.
        var html = "<p>America&#160;<a href=\"ch2.xhtml\">banned</a>&#160;imports.</p>";
        var r = EpubLinkExtractor.Extract(html);

        Assert.Equal(EpubDocumentReader.HtmlToText(html), r.Text);
        Assert.Equal("America\u00A0 banned \u00A0imports.", r.Text);
    }

    [Fact]
    public void A_link_with_only_whitespace_for_text_is_dropped()
    {
        var r = EpubLinkExtractor.Extract("<p>Before <a href=\"ch2.xhtml\"> </a> after.</p>");
        Assert.Empty(r.Links);
    }

    [Fact]
    public void Html_already_containing_a_sentinel_yields_no_extraction()
    {
        // Private-use characters do occur in the wild (icon fonts). They are indistinguishable from the
        // extractor's own markers, so the whole chapter is refused: empty text can never equal the reader's,
        // so EpubDocumentReader drops its links rather than pairing hrefs with the wrong spans.
        var r = EpubLinkExtractor.Extract("<p>Icon \uE000 <a href=\"ch2.xhtml\">link</a>.</p>");

        Assert.Equal(string.Empty, r.Text);
        Assert.Empty(r.Links);
        Assert.Empty(r.Anchors);
    }
}
