using BabelRead.Core.Domain;

namespace BabelRead.Core.Preferences;

/// <summary>Loads/saves non-secret reader preferences (JSON, per-user app data). Never stores secrets (FR-012).</summary>
public interface IPreferencesStore
{
    Task<ReaderPreferences> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(ReaderPreferences preferences, CancellationToken ct = default);
}
