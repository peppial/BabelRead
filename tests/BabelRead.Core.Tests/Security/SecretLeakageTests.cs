using System.Text;
using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using BabelRead.Core.Preferences;
using Xunit;

namespace BabelRead.Core.Tests.Security;

/// <summary>The reader's API key is the only real secret this app holds. FR-012 says it lives in the
/// OS secure store and never in the preferences file; these tests read the bytes on disk and prove it,
/// because a refactor that starts persisting the key would otherwise look perfectly correct.</summary>
[Trait("Category", "Security")]
public sealed class SecretLeakageTests : IDisposable
{
    private const string ApiKey = "sk-live-DO-NOT-PERSIST-8f3a91c2e7b45d06";

    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-security").FullName;

    private string PrefsPath => Path.Combine(_dir, "prefs.json");

    [Fact]
    public async Task Cloud_api_key_never_reaches_the_preferences_file()
    {
        var service = new ModelProfileService(
            new JsonPreferencesStore(PrefsPath), new InMemorySecretStore(), new NoLocalModels());
        await service.LoadAsync();

        await service.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", new Uri("https://api.openai.com/v1"), ApiKey);

        var onDisk = await File.ReadAllTextAsync(PrefsPath);
        Assert.DoesNotContain(ApiKey, onDisk, StringComparison.Ordinal);
        Assert.Contains("model:openai", onDisk, StringComparison.Ordinal); // only the reference
    }

    [Fact]
    public async Task Key_is_absent_from_the_raw_bytes_under_any_encoding_or_escaping()
    {
        var service = new ModelProfileService(
            new JsonPreferencesStore(PrefsPath), new InMemorySecretStore(), new NoLocalModels());
        await service.LoadAsync();
        await service.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", null, ApiKey);

        var bytes = await File.ReadAllBytesAsync(PrefsPath);

        // A JSON serializer could escape the key rather than omit it; check the bytes, not the string.
        Assert.DoesNotContain(ApiKey, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(ApiKey)), Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        // The key's distinctive middle, in case escaping broke it across the '-' boundaries.
        Assert.DoesNotContain("8f3a91c2e7b45d06", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reloading_a_profile_yields_a_secret_reference_and_not_the_key()
    {
        var prefs = new JsonPreferencesStore(PrefsPath);
        var secrets = new InMemorySecretStore();
        var service = new ModelProfileService(prefs, secrets, new NoLocalModels());
        await service.LoadAsync();
        await service.AddCloudProfileAsync("openai", "OpenAI", "gpt-4o-mini", null, ApiKey);

        var reloaded = new ModelProfileService(prefs, secrets, new NoLocalModels());
        await reloaded.LoadAsync();
        var profile = Assert.Single(reloaded.Profiles, p => p.ProfileId == "openai");

        Assert.True(profile.CredentialRef.HasValue);
        Assert.NotEqual(ApiKey, profile.CredentialRef.Value);
        Assert.Equal(ApiKey, await secrets.GetAsync(profile.CredentialRef)); // resolvable only through the store
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Keeps the service off the network: discovery must not decide whether these tests pass.</summary>
    private sealed class NoLocalModels : IOllamaModelCatalog
    {
        public Task<IReadOnlyList<string>> ListAvailableModelsAsync(Uri endpoint, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
