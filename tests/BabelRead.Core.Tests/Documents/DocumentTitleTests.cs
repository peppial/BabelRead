using BabelRead.Core.Documents;
using Xunit;

namespace BabelRead.Core.Tests.Documents;

public class DocumentTitleTests
{
    [Fact]
    public void Maps_windows_1252_apostrophe_control_byte_to_a_real_apostrophe()
    {
        Assert.Equal("Don\u2019t Panic", DocumentTitle.Clean("Don\u0092t Panic"));
    }

    [Fact]
    public void Maps_smart_quotes_and_dashes()
    {
        Assert.Equal("\u201CHi\u201D \u2013 \u2026", DocumentTitle.Clean("\u0093Hi\u0094 \u0096 \u0085"));
    }

    [Fact]
    public void Drops_unmapped_control_characters_without_leaving_a_gap()
    {
        // U+0090 is the real reported case: it renders as a tofu box between letters. Dropping it keeps the
        // title clean (the apostrophe cannot be recovered from a bare control byte).
        Assert.Equal("Centaurs Guide", DocumentTitle.Clean("Centaur\u0090s Guide"));
    }

    [Fact]
    public void Collapses_whitespace_left_by_dropped_characters()
    {
        Assert.Equal("A B", DocumentTitle.Clean("A \u0090 B"));
    }

    [Fact]
    public void Leaves_a_clean_title_unchanged()
    {
        Assert.Equal("The Reverse Centaur", DocumentTitle.Clean("The Reverse Centaur"));
    }

    [Fact]
    public void Empty_or_null_becomes_empty()
    {
        Assert.Equal(string.Empty, DocumentTitle.Clean(null));
        Assert.Equal(string.Empty, DocumentTitle.Clean(""));
    }
}
