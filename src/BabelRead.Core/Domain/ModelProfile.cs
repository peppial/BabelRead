namespace BabelRead.Core.Domain;

/// <summary>Whether the model runs in the cloud (reader-supplied credentials) or locally.
/// Consumer chat subscriptions are deliberately not a valid kind (FR-014).</summary>
public enum ModelKind
{
    Cloud,
    Local,
}

/// <summary>An opaque reference into <c>ISecretStore</c>; the app never handles raw keys.</summary>
public readonly record struct SecretRef(string Value)
{
    public bool HasValue => !string.IsNullOrEmpty(Value);
}

/// <summary>The reader's selected model and how to reach it (spec entity: Model Configuration).</summary>
public sealed class ModelProfile
{
    public ModelProfile(string profileId, string displayName, ModelKind kind, string modelId, Uri? endpoint = null, SecretRef credentialRef = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ProfileId = profileId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? modelId : displayName;
        Kind = kind;
        ModelId = modelId;
        Endpoint = endpoint;
        CredentialRef = credentialRef;
    }

    public string ProfileId { get; }

    public string DisplayName { get; }

    public ModelKind Kind { get; }

    /// <summary>Provider model name or local model tag.</summary>
    public string ModelId { get; }

    /// <summary>Base URL: local endpoint, or Azure resource; null for a provider default.</summary>
    public Uri? Endpoint { get; }

    /// <summary>Reference into the secret store (cloud only); never the raw key.</summary>
    public SecretRef CredentialRef { get; }
}
