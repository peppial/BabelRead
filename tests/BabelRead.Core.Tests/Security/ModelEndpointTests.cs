using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using BabelRead.Core.Preferences;
using Xunit;

namespace BabelRead.Core.Tests.Security;

/// <summary>A cloud profile's endpoint decides where the reader's API key is sent. An endpoint the app
/// accepts without checking is a credential-exfiltration primitive: plaintext http puts the key on the
/// wire, and a non-http scheme hands it to whatever else the client will dereference. The read path is
/// tested as well as the write path, because preferences.json is an editable file on disk.</summary>
[Trait("Category", "Security")]
public sealed class ModelEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-endpoints").FullName;

    private (ModelProfileService Service, JsonPreferencesStore Prefs) Create()
    {
        var prefs = new JsonPreferencesStore(Path.Combine(_dir, "prefs.json"));
        return (new ModelProfileService(prefs, new InMemorySecretStore(), new NoLocalModels()), prefs);
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]          // plaintext to a remote host: key readable in transit
    [InlineData("http://198.51.100.7:8080/v1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("gopher://example.com/")]
    public async Task Cloud_profile_rejects_an_endpoint_that_would_expose_the_key(string endpoint)
    {
        var (service, _) = Create();
        await service.LoadAsync();

        await Assert.ThrowsAsync<ModelConfigurationException>(() =>
            service.AddCloudProfileAsync("bad", "Bad", "gpt-4o-mini", new Uri(endpoint), "sk-secret"));

        Assert.DoesNotContain(service.Profiles, p => p.ProfileId == "bad");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://my-resource.openai.azure.com/")]
    [InlineData("http://localhost:11434/v1")]          // loopback never leaves the machine
    [InlineData("http://127.0.0.1:1234/v1")]
    public async Task Cloud_profile_accepts_https_or_a_loopback_endpoint(string endpoint)
    {
        var (service, _) = Create();
        await service.LoadAsync();

        var profile = await service.AddCloudProfileAsync("ok", "Ok", "gpt-4o-mini", new Uri(endpoint), "sk-secret");

        Assert.Equal(new Uri(endpoint), profile.Endpoint);
    }

    [Fact]
    public async Task Cloud_profile_with_no_endpoint_is_still_allowed()
    {
        var (service, _) = Create();
        await service.LoadAsync();

        var profile = await service.AddCloudProfileAsync("default", "Default", "gpt-4o-mini", null, "sk-secret");

        Assert.Null(profile.Endpoint); // the provider default, which the SDK resolves over https
    }

    [Fact]
    public async Task A_hand_edited_preferences_file_cannot_smuggle_a_plaintext_endpoint_past_the_ui()
    {
        var path = Path.Combine(_dir, "prefs.json");
        await File.WriteAllTextAsync(path, """
        {
          "ActiveModelProfileId": "smuggled",
          "ModelProfiles": [
            {
              "ProfileId": "smuggled",
              "DisplayName": "Smuggled",
              "Kind": "Cloud",
              "ModelId": "gpt-4o-mini",
              "Endpoint": "http://attacker.example.com/v1",
              "CredentialName": "model:smuggled"
            }
          ]
        }
        """);

        var service = new ModelProfileService(new JsonPreferencesStore(path), new InMemorySecretStore(), new NoLocalModels());
        await service.LoadAsync();

        Assert.DoesNotContain(service.Profiles, p => p.ProfileId == "smuggled");
        Assert.NotEqual("smuggled", service.Active.ProfileId);
    }

    [Fact]
    public async Task Local_profile_rejects_a_non_loopback_endpoint()
    {
        var (service, _) = Create();
        await service.LoadAsync();

        // A "local" profile carries no key, but a remote endpoint under that label sends every page
        // of the reader's book to a third party while the UI says the model runs on this machine.
        await Assert.ThrowsAsync<ModelConfigurationException>(() =>
            service.AddLocalProfileAsync("fake-local", "Local", "llama3.1", new Uri("http://attacker.example.com/v1")));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class NoLocalModels : IOllamaModelCatalog
    {
        public Task<IReadOnlyList<string>> ListAvailableModelsAsync(Uri endpoint, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
