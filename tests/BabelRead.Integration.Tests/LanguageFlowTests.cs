using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Integration.Tests;

public class LanguageFlowTests
{
    private static readonly ModelProfile Model = new("p1", "Local", ModelKind.Local, "test-model");

    [Fact]
    public async Task Same_page_translated_into_two_target_languages_produces_two_results()
    {
        var service = new TranslationService(new StubChatClientFactory(new FakeChatClient()), new InMemoryTranslationStore());
        var doc = new Document("d", "Doc", "/tmp/d.pdf", DocumentFormat.Pdf, 1, new LanguageCode("fr"));
        var page = new Page(0, "Bonjour");

        var toEnglish = await service.TranslateAsync(doc, page, new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);
        var toGerman = await service.TranslateAsync(doc, page, new LanguageCode("de"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal("en", toEnglish.TargetLanguage.Code);
        Assert.Equal("de", toGerman.TargetLanguage.Code);
        Assert.Equal(TranslationStatus.Completed, toEnglish.Status);
        Assert.Equal(TranslationStatus.Completed, toGerman.Status);
    }

    [Fact]
    public async Task Source_override_is_honoured_on_subsequent_translations()
    {
        var service = new TranslationService(new StubChatClientFactory(new FakeChatClient()), new InMemoryTranslationStore());
        // Detected source is English, but the reader overrides it to German.
        var doc = new Document("d", "Doc", "/tmp/d.pdf", DocumentFormat.Pdf, 1, new LanguageCode("en"));
        var page = new Page(0, "Hallo");

        var result = await service.TranslateAsync(doc, page, new LanguageCode("fr"), new LanguageCode("de"), Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal("de", result.SourceLanguage.Code); // override wins over detected "en"
    }
}
