using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using Xunit;

namespace BabelRead.Core.Tests.Translation;

public class TranslationCacheTests
{
    private static TranslationKey Key(int page, string lang, string model) =>
        new("doc-1", page, new LanguageCode(lang), model);

    private static PageTranslation Result(int page, string lang, string model) =>
        PageTranslation.Completed(page, new LanguageCode(lang), new LanguageCode("fr"), model, "text", TranslationOrigin.OnDemand);

    [Fact]
    public void Set_then_TryGet_returns_the_value()
    {
        var cache = new TranslationCache();
        var key = Key(3, "en", "m1");
        cache.Set(key, Result(3, "en", "m1"));

        Assert.True(cache.TryGet(key, out var value));
        Assert.Equal(3, value.PageIndex);
    }

    [Fact]
    public void Different_target_language_is_a_cache_miss()
    {
        var cache = new TranslationCache();
        cache.Set(Key(3, "en", "m1"), Result(3, "en", "m1"));

        Assert.False(cache.TryGet(Key(3, "de", "m1"), out _));
    }

    [Fact]
    public void Different_model_is_a_cache_miss()
    {
        var cache = new TranslationCache();
        cache.Set(Key(3, "en", "m1"), Result(3, "en", "m1"));

        Assert.False(cache.TryGet(Key(3, "en", "m2"), out _));
    }

    [Fact]
    public void Evicts_least_recently_used_when_over_capacity()
    {
        var cache = new TranslationCache(capacity: 2);
        cache.Set(Key(1, "en", "m1"), Result(1, "en", "m1"));
        cache.Set(Key(2, "en", "m1"), Result(2, "en", "m1"));
        // Touch page 1 so page 2 becomes least-recently-used.
        Assert.True(cache.TryGet(Key(1, "en", "m1"), out _));
        cache.Set(Key(3, "en", "m1"), Result(3, "en", "m1"));

        Assert.True(cache.TryGet(Key(1, "en", "m1"), out _));
        Assert.False(cache.TryGet(Key(2, "en", "m1"), out _)); // evicted
        Assert.True(cache.TryGet(Key(3, "en", "m1"), out _));
    }

    [Fact]
    public void Clear_removes_all_entries()
    {
        var cache = new TranslationCache();
        cache.Set(Key(1, "en", "m1"), Result(1, "en", "m1"));
        cache.Clear();

        Assert.False(cache.TryGet(Key(1, "en", "m1"), out _));
    }
}
