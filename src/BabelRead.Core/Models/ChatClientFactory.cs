using System.ClientModel;
using BabelRead.Core.Domain;
using BabelRead.Core.Preferences;
using Microsoft.Extensions.AI;
using OpenAI;

namespace BabelRead.Core.Models;

/// <summary>
/// Builds an <see cref="IChatClient"/> from a <see cref="ModelProfile"/> using the OpenAI-compatible
/// client, which serves both cloud providers (OpenAI / Azure OpenAI, reader-supplied key) and local
/// runtimes that expose the same wire API (Ollama, Foundry Local, LM Studio) via a base-URL override.
/// This is the single provider-swap seam (FR-007 / FR-014); consumer subscriptions are not a kind.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private const string LocalPlaceholderKey = "local-no-key-required";
    private readonly ISecretStore _secrets;

    public ChatClientFactory(ISecretStore secrets) => _secrets = secrets;

    public IChatClient Create(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Kind switch
        {
            ModelKind.Local => CreateOpenAiCompatible(profile, LocalPlaceholderKey),
            ModelKind.Cloud => CreateCloud(profile),
            _ => throw new ModelConfigurationException($"Unsupported model kind '{profile.Kind}'."),
        };
    }

    private IChatClient CreateCloud(ModelProfile profile)
    {
        if (!profile.CredentialRef.HasValue)
        {
            throw new ModelConfigurationException($"Cloud model profile '{profile.ProfileId}' has no credential configured.");
        }

        // Keychain read is fast and this runs off the UI thread; resolving synchronously keeps the
        // provider-swap seam a simple synchronous factory.
        var key = _secrets.GetAsync(profile.CredentialRef).ConfigureAwait(false).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(key))
        {
            throw new ModelConfigurationException($"No stored credential for cloud model profile '{profile.ProfileId}'.");
        }

        return CreateOpenAiCompatible(profile, key);
    }

    private static IChatClient CreateOpenAiCompatible(ModelProfile profile, string apiKey)
    {
        var options = new OpenAIClientOptions();
        if (profile.Endpoint is not null)
        {
            options.Endpoint = profile.Endpoint;
        }

        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        return client.GetChatClient(profile.ModelId).AsIChatClient();
    }
}
