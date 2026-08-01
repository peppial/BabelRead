using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Integration.Tests;

public class ModelSwitchTests
{
    [Fact]
    public async Task Switching_the_active_model_changes_which_client_produces_the_translation()
    {
        // Two distinct fake models; the stub factory routes by the profile's model id.
        var modelA = new FakeChatClient(s => "A:" + s);
        var modelB = new FakeChatClient(s => "B:" + s);
        var factory = new StubChatClientFactory(profile => profile.ModelId == "model-a" ? modelA : modelB);
        var service = new TranslationService(factory, new InMemoryTranslationStore());

        var doc = new Document("d", "Doc", "/tmp/d.pdf", DocumentFormat.Pdf, 1, new LanguageCode("fr"));
        var page = new Page(0, "Bonjour");

        var profileA = new ModelProfile("a", "A", ModelKind.Local, "model-a");
        var withA = await service.TranslateAsync(doc, page, new LanguageCode("en"), null, profileA, TranslationOrigin.OnDemand, CancellationToken.None);
        Assert.StartsWith("A:", withA.Text, StringComparison.Ordinal);

        // Switch model → next translation is produced by the other client.
        var profileB = new ModelProfile("b", "B", ModelKind.Local, "model-b");
        var withB = await service.TranslateAsync(doc, page, new LanguageCode("en"), null, profileB, TranslationOrigin.OnDemand, CancellationToken.None);
        Assert.StartsWith("B:", withB.Text, StringComparison.Ordinal);

        Assert.Equal("model-b", withB.ModelId);
    }
}
