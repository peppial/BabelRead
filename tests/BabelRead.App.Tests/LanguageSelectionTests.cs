using BabelRead.App.ViewModels;
using BabelRead.Core.Documents;
using BabelRead.Core.Domain;
using BabelRead.Core.Preferences;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.App.Tests;

public sealed class LanguageSelectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-lang").FullName;
    private readonly FakeChatClient _fake = new();

    private ReaderViewModel CreateViewModel(out JsonPreferencesStore prefs)
    {
        prefs = new JsonPreferencesStore(Path.Combine(_dir, "prefs.json"));
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() });
        return new ReaderViewModel(registry, new TranslationService(new StubChatClientFactory(_fake)), new TranslationCache(), new NoOpPrefetchCoordinator(), prefs);
    }

    [Fact]
    public async Task Changing_target_language_re_translates_the_current_page()
    {
        var vm = CreateViewModel(out _);
        await vm.OpenAsync(SampleDocuments.CreatePdf(Path.Combine(_dir, "a.pdf"), "Bonjour"));
        var callsAfterOpen = _fake.CallCount;

        await vm.SetTargetLanguageAsync(new LanguageCode("de"));

        Assert.True(_fake.CallCount > callsAfterOpen); // re-translated for the new language
        Assert.Equal(ReaderState.Content, vm.State);
        Assert.Equal("de", vm.TargetLanguage.Code);
    }

    [Fact]
    public async Task Target_language_choice_persists()
    {
        var vm = CreateViewModel(out var prefs);
        await vm.OpenAsync(SampleDocuments.CreatePdf(Path.Combine(_dir, "b.pdf"), "Bonjour"));

        await vm.SetTargetLanguageAsync(new LanguageCode("es"));

        var reloaded = await prefs.LoadAsync();
        Assert.Equal("es", reloaded.TargetLanguage.Code);
    }

    [Fact]
    public async Task Source_override_persists_per_document_and_re_translates()
    {
        var vm = CreateViewModel(out var prefs);
        await vm.OpenAsync(SampleDocuments.CreatePdf(Path.Combine(_dir, "c.pdf"), "Hallo"));
        var callsAfterOpen = _fake.CallCount;

        await vm.SetSourceOverrideAsync(new LanguageCode("de"));

        Assert.True(_fake.CallCount > callsAfterOpen);
        var reloaded = await prefs.LoadAsync();
        Assert.NotEmpty(reloaded.SourceLanguageOverrides);
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
