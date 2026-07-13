using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using Xunit;

namespace BabelRead.Core.Tests.Translation;

public class LanguageResolutionTests
{
    private static Document Doc(string detected) =>
        new("doc-1", "Doc", "/tmp/d.pdf", DocumentFormat.Pdf, 5, new LanguageCode(detected));

    [Fact]
    public void Override_takes_precedence_over_detected()
    {
        var source = LanguageResolver.ResolveSource(Doc("en"), new LanguageCode("de"));
        Assert.Equal("de", source.Code);
    }

    [Fact]
    public void Falls_back_to_detected_when_no_override()
    {
        var source = LanguageResolver.ResolveSource(Doc("fr"), overrideForDocument: null);
        Assert.Equal("fr", source.Code);
    }

    [Fact]
    public void Unknown_override_falls_back_to_detected()
    {
        var source = LanguageResolver.ResolveSource(Doc("fr"), LanguageCode.Unknown);
        Assert.Equal("fr", source.Code);
    }

    [Fact]
    public void Set_and_get_override_round_trips_per_document()
    {
        var prefs = new ReaderPreferences();
        LanguageResolver.SetOverride(prefs, "doc-1", new LanguageCode("es"));

        Assert.Equal("es", LanguageResolver.GetOverride(prefs, "doc-1")!.Value.Code);
        Assert.Null(LanguageResolver.GetOverride(prefs, "doc-2"));
    }

    [Fact]
    public void Setting_unknown_override_clears_it()
    {
        var prefs = new ReaderPreferences();
        LanguageResolver.SetOverride(prefs, "doc-1", new LanguageCode("es"));
        LanguageResolver.SetOverride(prefs, "doc-1", LanguageCode.Unknown);

        Assert.Null(LanguageResolver.GetOverride(prefs, "doc-1"));
    }
}
