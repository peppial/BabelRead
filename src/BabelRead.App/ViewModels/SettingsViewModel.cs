using System.Collections.ObjectModel;
using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BabelRead.App.ViewModels;

/// <summary>
/// Model-configuration surface (US2). Lists the reader's model profiles (cloud + local only — no
/// consumer subscriptions, FR-014), lets the reader add profiles and switch the active one; switching
/// pushes the new model into the reader so later translations use it (FR-007) and re-translates the
/// current page so the change is visible (SC-004).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ModelProfileService _profiles;
    private readonly ReaderViewModel _reader;

    [ObservableProperty]
    private ModelProfile? _activeProfile;

    public SettingsViewModel(ModelProfileService profiles, ReaderViewModel reader)
    {
        _profiles = profiles;
        _reader = reader;
    }

    public ObservableCollection<ModelProfile> Profiles { get; } = new();

    /// <summary>Background-translation modes, as the Settings window shows them.</summary>
    public static IReadOnlyList<string> BackgroundTranslationOptions { get; } = new[]
    {
        "Off — only a couple of pages ahead",
        "Gentle — pause between pages (cooler)",
        "Full speed — no pauses (hotter, ~2x faster)",
    };

    /// <summary>The reader's current mode, as one of <see cref="BackgroundTranslationOptions"/>. Setting it
    /// applies and persists the mode.</summary>
    public string BackgroundTranslationLabel
    {
        get => BackgroundTranslationOptions[(int)_reader.BackgroundTranslation];
        set
        {
            var index = BackgroundTranslationOptions.ToList().IndexOf(value);
            if (index < 0 || (BackgroundTranslation)index == _reader.BackgroundTranslation)
            {
                return;
            }

            _ = _reader.SetBackgroundTranslationAsync((BackgroundTranslation)index);
            OnPropertyChanged();
        }
    }

    /// <summary>Only local models run without any account or key; cloud models need reader-supplied
    /// credentials. Consumer chat subscriptions (Copilot, claude.ai) are intentionally not an option.</summary>
    public static IReadOnlyList<string> AvailableKindLabels { get; } = new[] { "Local (no key)", "Cloud (your API key)" };

    public async Task LoadAsync()
    {
        await _profiles.LoadAsync().ConfigureAwait(true);
        RefreshProfiles();
        ActiveProfile = _profiles.Active;
        _reader.ActiveModel = _profiles.Active;
    }

    public async Task AddCloudProfileAsync(string profileId, string displayName, string modelId, Uri? endpoint, string apiKey)
    {
        await _profiles.AddCloudProfileAsync(profileId, displayName, modelId, endpoint, apiKey).ConfigureAwait(true);
        RefreshProfiles();
    }

    public async Task AddLocalProfileAsync(string profileId, string displayName, string modelId, Uri endpoint)
    {
        await _profiles.AddLocalProfileAsync(profileId, displayName, modelId, endpoint).ConfigureAwait(true);
        RefreshProfiles();
    }

    /// <summary>Switch the active model. Later translations — including a re-translation of the current
    /// page — use the new model.</summary>
    public async Task SetActiveAsync(string profileId)
    {
        await _profiles.SetActiveAsync(profileId).ConfigureAwait(true);
        ActiveProfile = _profiles.Active;
        _reader.ActiveModel = _profiles.Active;
        await _reader.RetryAsync().ConfigureAwait(true); // re-translate current page with the new model
    }

    /// <summary>Apply a target language (BCP-47 code); the reader persists it and re-translates (US3).</summary>
    public Task ApplyTargetLanguageAsync(string code) =>
        _reader.SetTargetLanguageAsync(new LanguageCode((code ?? string.Empty).Trim()));

    /// <summary>Override the detected source language for the current document (empty clears it).</summary>
    public Task ApplySourceOverrideAsync(string? code) =>
        _reader.SetSourceOverrideAsync(string.IsNullOrWhiteSpace(code) ? null : new LanguageCode(code.Trim()));

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var p in _profiles.Profiles)
        {
            Profiles.Add(p);
        }
    }
}
