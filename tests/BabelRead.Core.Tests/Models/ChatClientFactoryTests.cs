using BabelRead.Core.Domain;
using BabelRead.Core.Models;
using BabelRead.Core.Preferences;
using Xunit;

namespace BabelRead.Core.Tests.Models;

public class ChatClientFactoryTests
{
    [Fact]
    public void Builds_a_local_client_with_no_credential()
    {
        var factory = new ChatClientFactory(new InMemorySecretStore());
        var profile = ModelProfiles.DefaultLocal();

        using var client = factory.Create(profile);

        Assert.NotNull(client);
    }

    [Fact]
    public async Task Builds_a_cloud_client_when_a_credential_is_stored()
    {
        var secrets = new InMemorySecretStore();
        var secretRef = await secrets.SetAsync("model:cloud1", "sk-test-key");
        var factory = new ChatClientFactory(secrets);
        var profile = new ModelProfile("cloud1", "OpenAI", ModelKind.Cloud, "gpt-4o-mini", endpoint: null, credentialRef: secretRef);

        using var client = factory.Create(profile);

        Assert.NotNull(client);
    }

    [Fact]
    public void Cloud_profile_without_credential_throws()
    {
        var factory = new ChatClientFactory(new InMemorySecretStore());
        var profile = new ModelProfile("cloud1", "OpenAI", ModelKind.Cloud, "gpt-4o-mini");

        Assert.Throws<ModelConfigurationException>(() => factory.Create(profile));
    }
}
