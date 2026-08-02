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
}
