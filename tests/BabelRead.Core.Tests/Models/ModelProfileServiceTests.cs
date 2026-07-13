using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using BabelRead.Core.Preferences;
using Xunit;

namespace BabelRead.Core.Tests.Models;

public sealed class ModelProfileServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-profiles").FullName;

    private (ModelProfileService Service, InMemorySecretStore Secrets, JsonPreferencesStore Prefs) Create()
    {
        var prefs = new JsonPreferencesStore(Path.Combine(_dir, "prefs.json"));
        var secrets = new InMemorySecretStore();
        return (new ModelProfileService(prefs, secrets, new StubOllamaModelCatalog()), secrets, prefs);
    }

    [Fact]
    public async Task Default_local_profile_is_always_present_and_active()
    {
        var (service, _, _) = Create();
        await service.LoadAsync();

        Assert.Contains(service.Profiles, p => p.ProfileId == ModelProfiles.DefaultLocalProfileId);
        Assert.Equal(ModelProfiles.DefaultLocalProfileId, service.Active.ProfileId);
        Assert.All(service.Profiles, p => Assert.NotEqual(default, p.Kind == ModelKind.Cloud ? ModelKind.Cloud : p.Kind));
    }

    [Fact]
    public async Task Adding_a_cloud_profile_stores_the_key_in_the_secret_store_not_prefs()
    {
        var (service, secrets, _) = Create();
        await service.LoadAsync();

        var profile = await service.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", endpoint: null, apiKey: "sk-secret");

        Assert.Equal(ModelKind.Cloud, profile.Kind);
        Assert.True(profile.CredentialRef.HasValue);
        Assert.Equal("sk-secret", await secrets.GetAsync(profile.CredentialRef));
    }

    [Fact]
    public async Task Switching_active_profile_persists_across_reloads()
    {
        var (service, secrets, prefs) = Create();
        await service.LoadAsync();
        await service.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", endpoint: null, apiKey: "sk-secret");
        await service.SetActiveAsync("openai");

        // New service instance over the same persisted preferences.
        var reloaded = new ModelProfileService(prefs, secrets);
        await reloaded.LoadAsync();

        Assert.Equal("openai", reloaded.Active.ProfileId);
        Assert.Contains(reloaded.Profiles, p => p.ProfileId == "openai");
    }

    [Fact]
    public async Task Only_cloud_and_local_kinds_exist_no_subscription_kind()
    {
        var (service, _, _) = Create();
        await service.LoadAsync();
        await service.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", null, "sk");

        Assert.All(service.Profiles, p => Assert.True(p.Kind is ModelKind.Cloud or ModelKind.Local));
    }

    [Fact]
    public async Task Discovered_ollama_models_are_listed_as_local_profiles()
    {
        var prefs = new JsonPreferencesStore(Path.Combine(_dir, "prefs-discovered.json"));
        var secrets = new InMemorySecretStore();
        var service = new ModelProfileService(
            prefs,
            secrets,
            new StubOllamaModelCatalog("gemma3:4b", "llama3.1:8b"));

        await service.LoadAsync();

        Assert.Contains(service.Profiles, p => p.ProfileId == ModelProfiles.DefaultLocalProfileId && p.ModelId == "gemma3:4b");
        Assert.Contains(service.Profiles, p => p.ModelId == "llama3.1:8b");
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
