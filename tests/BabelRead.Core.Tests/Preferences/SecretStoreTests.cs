using BabelRead.Core.Domain;
using BabelRead.Core.Preferences;
using Xunit;

namespace BabelRead.Core.Tests.Preferences;

public class InMemorySecretStoreTests
{
    [Fact]
    public async Task Set_then_Get_round_trips_the_secret()
    {
        var store = new InMemorySecretStore();
        var reference = await store.SetAsync("api-key", "s3cr3t");

        Assert.Equal("s3cr3t", await store.GetAsync(reference));
    }

    [Fact]
    public async Task Get_unknown_reference_returns_null()
    {
        var store = new InMemorySecretStore();
        Assert.Null(await store.GetAsync(new SecretRef("missing")));
    }

    [Fact]
    public async Task Remove_deletes_the_secret()
    {
        var store = new InMemorySecretStore();
        var reference = await store.SetAsync("api-key", "s3cr3t");
        await store.RemoveAsync(reference);

        Assert.Null(await store.GetAsync(reference));
    }
}
