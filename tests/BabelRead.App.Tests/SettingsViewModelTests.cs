using BabelRead.App.ViewModels;
using BabelRead.Core.Documents;
using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using BabelRead.Core.Preferences;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.App.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-settings").FullName;

    private (SettingsViewModel Settings, ReaderViewModel Reader) Create()
    {
        var prefs = new JsonPreferencesStore(Path.Combine(_dir, "prefs.json"));
        var secrets = new InMemorySecretStore();
        var profiles = new ModelProfileService(prefs, secrets, new StubOllamaModelCatalog());
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() });
        var reader = new ReaderViewModel(registry, new TranslationService(new StubChatClientFactory(new FakeChatClient())), new TranslationCache(), new NoOpPrefetchCoordinator(), prefs);
        return (new SettingsViewModel(profiles, reader), reader);
    }

    [Fact]
    public async Task Load_lists_the_default_local_profile()
    {
        var (settings, _) = Create();
        await settings.LoadAsync();

        Assert.Contains(settings.Profiles, p => p.ProfileId == ModelProfiles.DefaultLocalProfileId);
        Assert.NotNull(settings.ActiveProfile);
    }

    [Fact]
    public async Task Adding_a_cloud_profile_appears_in_the_list()
    {
        var (settings, _) = Create();
        await settings.LoadAsync();

        await settings.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", endpoint: null, apiKey: "sk-x");

        Assert.Contains(settings.Profiles, p => p.ProfileId == "openai" && p.Kind == ModelKind.Cloud);
    }

    [Fact]
    public async Task Switching_the_model_updates_the_reader_active_model()
    {
        var (settings, reader) = Create();
        await settings.LoadAsync();
        await settings.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", endpoint: null, apiKey: "sk-x");

        await settings.SetActiveAsync("openai");

        Assert.Equal("openai", reader.ActiveModel.ProfileId);
        Assert.Equal("openai", settings.ActiveProfile!.ProfileId);
    }

    [Fact]
    public async Task Offered_profiles_are_only_cloud_or_local_never_a_subscription()
    {
        var (settings, _) = Create();
        await settings.LoadAsync();
        await settings.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", null, "sk-x");

        Assert.All(settings.Profiles, p => Assert.True(p.Kind is ModelKind.Cloud or ModelKind.Local));
        Assert.DoesNotContain(SettingsViewModel.AvailableKindLabels, label => label.Contains("Copilot", StringComparison.OrdinalIgnoreCase) || label.Contains("subscription", StringComparison.OrdinalIgnoreCase));
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

    private sealed class StubOllamaModelCatalog(params string[] models) : IOllamaModelCatalog
    {
        private readonly string[] _models = models;

        public Task<IReadOnlyList<string>> ListAvailableModelsAsync(Uri endpoint, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(_models);
    }
}
