using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using Xunit;

namespace BabelRead.Core.Tests.Translation;

public sealed class JsonTranslationStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-store").FullName;

    private static TranslationKey Key(string text) =>
        TranslationKey.For(text, new LanguageCode("en"), new LanguageCode("bg"), "gpt-4o");

    [Fact]
    public async Task Saved_segment_is_written_to_disk_and_reloads_in_a_new_store()
    {
        var key = Key("Hello world");
        await using (var store = new JsonTranslationStore(_dir))
        {
            await store.OpenAsync("doc-1");
            await store.SaveAsync(key, "Здравей свят");
            await store.FlushAsync(); // force the debounced write to disk now
        }

        var path = new JsonTranslationStore(_dir).FilePathFor("doc-1");
        Assert.True(File.Exists(path), $"expected a translation file at {path}");

        var reloaded = new JsonTranslationStore(_dir);
        await reloaded.OpenAsync("doc-1");
        Assert.True(reloaded.TryGet(key, out var text));
        Assert.Equal("Здравей свят", text);
    }

    [Fact]
    public async Task A_burst_of_saves_all_reach_disk_after_flush()
    {
        await using var store = new JsonTranslationStore(_dir);
        await store.OpenAsync("doc-2");

        for (var i = 0; i < 50; i++)
        {
            await store.SaveAsync(Key($"segment {i}"), $"превод {i}");
        }

        await store.FlushAsync();

        var reloaded = new JsonTranslationStore(_dir);
        await reloaded.OpenAsync("doc-2");
        for (var i = 0; i < 50; i++)
        {
            Assert.True(reloaded.TryGet(Key($"segment {i}"), out _), $"segment {i} was lost");
        }
    }

    [Fact]
    public async Task The_last_save_survives_disposal_without_an_explicit_flush()
    {
        // Simulates the app quitting: the store is disposed, which must flush the debounced tail.
        var key = Key("final segment");
        await using (var store = new JsonTranslationStore(_dir))
        {
            await store.OpenAsync("doc-3");
            await store.SaveAsync(key, "последен превод");
            // No FlushAsync — DisposeAsync must persist it.
        }

        var reloaded = new JsonTranslationStore(_dir);
        await reloaded.OpenAsync("doc-3");
        Assert.True(reloaded.TryGet(key, out var text), "the tail segment was lost on disposal");
        Assert.Equal("последен превод", text);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
