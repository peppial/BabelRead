using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using Microsoft.Extensions.AI;

namespace BabelRead.TestSupport;

/// <summary>Returns a preconfigured <see cref="IChatClient"/> for any profile, or a per-profile-id client.
/// Lets tests inspect which model was used and how many calls were made.</summary>
public sealed class StubChatClientFactory : IChatClientFactory
{
    private readonly Func<ModelProfile, IChatClient> _resolve;

    public StubChatClientFactory(IChatClient client) => _resolve = _ => client;

    public StubChatClientFactory(Func<ModelProfile, IChatClient> resolve) => _resolve = resolve;

    public ModelProfile? LastProfile { get; private set; }

    public IChatClient Create(ModelProfile profile)
    {
        LastProfile = profile;
        return _resolve(profile);
    }
}
