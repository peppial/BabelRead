using BabelRead.Core.Domain;

namespace BabelRead.Core.Preferences;

/// <summary>Stores/retrieves secrets (cloud API keys) via OS-native secure storage.
/// The rest of the app handles only <see cref="SecretRef"/>, never raw keys.</summary>
public interface ISecretStore
{
    Task<SecretRef> SetAsync(string name, string secret, CancellationToken ct = default);

    Task<string?> GetAsync(SecretRef reference, CancellationToken ct = default);

    Task RemoveAsync(SecretRef reference, CancellationToken ct = default);
}
