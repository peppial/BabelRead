using BabelRead.Core.Domain;
using BabelRead.Core.Preferences;

namespace BabelRead.Core.Models;

/// <summary>
/// Manages the reader's model profiles and which one is active (FR-007). Supports cloud (reader-supplied
/// credentials, stored via <see cref="ISecretStore"/>) and local profiles only — consumer chat
/// subscriptions are never offered (FR-014). The profile list and active selection persist across
/// sessions (FR-012); secrets go to the secret store, never the preferences file.
/// </summary>
public sealed class ModelProfileService
{
    private const string DiscoveredLocalProfilePrefix = "ollama-local-";
    private readonly IPreferencesStore _preferences;
    private readonly ISecretStore _secrets;
    private readonly IOllamaModelCatalog _ollamaModelCatalog;
    private readonly List<ModelProfile> _profiles = new();
    private string _activeId = ModelProfiles.DefaultLocalProfileId;

    public ModelProfileService(IPreferencesStore preferences, ISecretStore secrets, IOllamaModelCatalog? ollamaModelCatalog = null)
    {
        _preferences = preferences;
        _secrets = secrets;
        _ollamaModelCatalog = ollamaModelCatalog ?? new OllamaModelCatalog();
    }

    public IReadOnlyList<ModelProfile> Profiles => _profiles;

    public ModelProfile Active =>
        _profiles.FirstOrDefault(p => p.ProfileId == _activeId) ?? _profiles[0];

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var prefs = await _preferences.LoadAsync(ct).ConfigureAwait(false);
        _profiles.Clear();

        var discoveredLocalProfiles = await BuildDiscoveredLocalProfilesAsync(ct).ConfigureAwait(false);
        if (discoveredLocalProfiles.Count == 0)
        {
            // Always provide the built-in local default so the app works with no configuration.
            _profiles.Add(ModelProfiles.DefaultLocal());
        }
        else
        {
            _profiles.AddRange(discoveredLocalProfiles);
        }

        foreach (var stored in prefs.ModelProfiles)
        {
            if (stored.ProfileId == ModelProfiles.DefaultLocalProfileId)
            {
                continue;
            }

            var mapped = Map(stored);
            var existing = _profiles.FindIndex(p => p.ProfileId == mapped.ProfileId);
            if (existing >= 0)
            {
                _profiles[existing] = mapped;
            }
            else
            {
                _profiles.Add(mapped);
            }
        }

        _activeId = !string.IsNullOrEmpty(prefs.ActiveModelProfileId) && _profiles.Any(p => p.ProfileId == prefs.ActiveModelProfileId)
            ? prefs.ActiveModelProfileId!
            : ModelProfiles.DefaultLocalProfileId;
    }

    private async Task<IReadOnlyList<ModelProfile>> BuildDiscoveredLocalProfilesAsync(CancellationToken ct)
    {
        var discoveredModelIds = await _ollamaModelCatalog.ListAvailableModelsAsync(ModelProfiles.DefaultLocal().Endpoint!, ct).ConfigureAwait(false);
        if (discoveredModelIds.Count == 0)
        {
            return [];
        }

        var endpoint = ModelProfiles.DefaultLocal().Endpoint!;
        var distinctIds = discoveredModelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctIds.Count == 0)
        {
            return [];
        }

        var profiles = new List<ModelProfile>(distinctIds.Count);
        for (var i = 0; i < distinctIds.Count; i++)
        {
            var modelId = distinctIds[i];
            var profileId = i == 0
                ? ModelProfiles.DefaultLocalProfileId
                : $"{DiscoveredLocalProfilePrefix}{ToKebab(modelId)}";
            profiles.Add(new ModelProfile(profileId, "Local (Ollama)", ModelKind.Local, modelId, endpoint));
        }

        return profiles;
    }

    private static string ToKebab(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "model";
        }

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        }

        return collapsed.Trim('-');
    }

    public async Task SetActiveAsync(string profileId, CancellationToken ct = default)
    {
        if (_profiles.All(p => p.ProfileId != profileId))
        {
            throw new ArgumentException($"Unknown model profile '{profileId}'.", nameof(profileId));
        }

        _activeId = profileId;
        await PersistAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Adds (or replaces) a cloud model profile, storing the API key in the secret store.</summary>
    public async Task<ModelProfile> AddCloudProfileAsync(string profileId, string displayName, string modelId, Uri? endpoint, string apiKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var secretRef = await _secrets.SetAsync($"model:{profileId}", apiKey, ct).ConfigureAwait(false);
        var profile = new ModelProfile(profileId, displayName, ModelKind.Cloud, modelId, endpoint, secretRef);

        _profiles.RemoveAll(p => p.ProfileId == profileId);
        _profiles.Add(profile);
        await PersistAsync(ct).ConfigureAwait(false);
        return profile;
    }

    /// <summary>Adds (or replaces) a local model profile (no key).</summary>
    public async Task<ModelProfile> AddLocalProfileAsync(string profileId, string displayName, string modelId, Uri endpoint, CancellationToken ct = default)
    {
        var profile = new ModelProfile(profileId, displayName, ModelKind.Local, modelId, endpoint);
        _profiles.RemoveAll(p => p.ProfileId == profileId);
        _profiles.Add(profile);
        await PersistAsync(ct).ConfigureAwait(false);
        return profile;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var prefs = await _preferences.LoadAsync(ct).ConfigureAwait(false);
        prefs.ActiveModelProfileId = _activeId;
        prefs.ModelProfiles.Clear();
        foreach (var p in _profiles.Where(p => p.ProfileId != ModelProfiles.DefaultLocalProfileId))
        {
            prefs.ModelProfiles.Add(new StoredModelProfile
            {
                ProfileId = p.ProfileId,
                DisplayName = p.DisplayName,
                Kind = p.Kind,
                ModelId = p.ModelId,
                Endpoint = p.Endpoint?.ToString(),
                CredentialName = p.CredentialRef.HasValue ? p.CredentialRef.Value : null,
            });
        }

        await _preferences.SaveAsync(prefs, ct).ConfigureAwait(false);
    }

    private static ModelProfile Map(StoredModelProfile s) =>
        new(
            s.ProfileId,
            s.DisplayName,
            s.Kind,
            s.ModelId,
            string.IsNullOrEmpty(s.Endpoint) ? null : new Uri(s.Endpoint),
            string.IsNullOrEmpty(s.CredentialName) ? default : new SecretRef(s.CredentialName));
}
