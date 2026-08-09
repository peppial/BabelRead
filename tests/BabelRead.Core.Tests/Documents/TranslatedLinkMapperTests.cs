using BabelRead.Core.Documents;
using Xunit;

namespace BabelRead.Core.Tests.Documents;

public class TranslatedLinkMapperTests
{
    /// <summary>The reader's menu bar, as it appears in a Calibre-built magazine and as a model translates
    /// it: the pipes survive, so each label can be matched to its counterpart by position.</summary>
    private const string MenuOriginal = "| Next | Section menu | Main menu | Previous |";
    private const string MenuTranslated = "| Следващ | Меню на раздела | Основно меню | Предишен |";

    private static string? Mapped(string translated, (int Start, int Length)? range) =>
        range is { } r ? translated.Substring(r.Start, r.Length) : null;

    [Fact]
    public void A_link_covering_a_whole_paragraph_maps_to_the_whole_translated_paragraph()
    {
        var mapped = TranslatedLinkMapper.Map("Europe", "Европа", [(0, 6)]);

        Assert.Equal("Европа", Mapped("Европа", Assert.Single(mapped)));
    }

    [Fact]
    public void Each_label_on_a_separated_line_maps_to_its_own_translated_label()
    {
        var mapped = TranslatedLinkMapper.Map(MenuOriginal, MenuTranslated, [(2, 4), (9, 12), (24, 9), (36, 8)]);

        Assert.Equal(
            ["Следващ", "Меню на раздела", "Основно меню", "Предишен"],
            mapped.Select(m => Mapped(MenuTranslated, m)));
    }

    [Fact]
    public void Every_link_in_a_coalesced_paragraph_run_maps_to_its_own_line()
    {
        // Short blocks are coalesced into one segment, so a table of contents arrives as one paragraph run.
        var mapped = TranslatedLinkMapper.Map(
            "Europe\n\nBritain\n\nCulture", "Европа\n\nБритания\n\nКултура", [(0, 6), (8, 7), (17, 7)]);

        Assert.Equal(
            ["Европа", "Британия", "Култура"],
            mapped.Select(m => Mapped("Европа\n\nБритания\n\nКултура", m)));
    }

    [Fact]
    public void A_translation_that_lost_the_structure_maps_nothing()
    {
        // The model dropped a separator, so labels can no longer be paired off by position. Underlining the
        // wrong words is worse than underlining none, so the whole paragraph is given up on.
        var mapped = TranslatedLinkMapper.Map(
            MenuOriginal, "Следващ | Меню на раздела | Основно меню", [(2, 4), (9, 12), (24, 9), (36, 8)]);

        Assert.All(mapped, m => Assert.Null(m));
    }

    [Fact]
    public void A_link_inside_a_sentence_maps_nothing()
    {
        // Word order moves under translation, so there is no telling which words carry the link.
        var mapped = TranslatedLinkMapper.Map(
            "Wildfires raged in Spain as temperatures soared.",
            "Пожари бушуваха в Испания, докато температурите се повишиха.",
            [(19, 5)]);

        Assert.Null(Assert.Single(mapped));
    }

    [Fact]
    public void Two_links_claiming_the_same_line_map_nothing()
    {
        // Nested anchors leave two links over one run of text; there is no telling them apart afterwards.
        var mapped = TranslatedLinkMapper.Map("See here now", "Виж тук сега", [(0, 12), (0, 12)]);

        Assert.All(mapped, m => Assert.Null(m));
    }

    [Fact]
    public void An_empty_or_untranslated_paragraph_maps_nothing()
    {
        Assert.Null(Assert.Single(TranslatedLinkMapper.Map("Europe", string.Empty, [(0, 6)])));
        Assert.Empty(TranslatedLinkMapper.Map("Europe", "Европа", []));
    }
}
