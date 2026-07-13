using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Core.Tests.Translation;

public class PrefetchCoordinatorTests
{
    private static readonly ModelProfile Model = new("p1", "Local", ModelKind.Local, "test-model");
    private static readonly Document Document = new("doc-1", "Doc", "/tmp/d.pdf", DocumentFormat.Pdf, 20, new LanguageCode("fr"));

    private static PrefetchContext Context(FakeChatClient fake, out TranslationCache cache)
    {
        cache = new TranslationCache();
        _ = new TranslationService(new StubChatClientFactory(fake)); // ensure service constructs
        return new PrefetchContext(
            Document,
            new LanguageCode("en"),
            SourceOverride: null,
            Model,
            GetPageAsync: (index, ct) => Task.FromResult<Page?>(new Page(index, $"page {index} text")));
    }

    [Fact]
    public async Task Prefetches_next_forward_page_into_cache()
    {
        var fake = new FakeChatClient();
        var context = Context(fake, out var cache);
        var service = new TranslationService(new StubChatClientFactory(fake));
        var coordinator = new PrefetchCoordinator(service, cache);

        coordinator.OnPageSettled(context, currentIndex: 5, ReadingDirection.Forward);
        await coordinator.PendingTask;

        Assert.True(cache.TryGet(new TranslationKey("doc-1", 6, new LanguageCode("en"), "test-model"), out var t));
        Assert.Equal(TranslationOrigin.Prefetch, t.Origin);
        Assert.True(cache.TryGet(new TranslationKey("doc-1", 19, new LanguageCode("en"), "test-model"), out _)); // runs ahead in background
    }

    [Fact]
    public async Task Does_not_prefetch_past_the_last_page()
    {
        var fake = new FakeChatClient();
        var context = Context(fake, out var cache);
        var coordinator = new PrefetchCoordinator(new TranslationService(new StubChatClientFactory(fake)), cache);

        coordinator.OnPageSettled(context, currentIndex: 19, ReadingDirection.Forward); // last page (count 20)
        await coordinator.PendingTask;

        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task Cancelling_stops_the_pending_prefetch_from_caching()
    {
        var fake = new FakeChatClient(delay: TimeSpan.FromMilliseconds(500));
        var context = Context(fake, out var cache);
        var coordinator = new PrefetchCoordinator(new TranslationService(new StubChatClientFactory(fake)), cache);

        coordinator.OnPageSettled(context, currentIndex: 5, ReadingDirection.Forward);
        coordinator.CancelPending();
        await coordinator.PendingTask; // completes (cancelled) without throwing

        Assert.False(cache.TryGet(new TranslationKey("doc-1", 6, new LanguageCode("en"), "test-model"), out _));
    }

    [Fact]
    public async Task Skips_cached_page_and_continues_prefetching_ahead()
    {
        var fake = new FakeChatClient();
        var context = Context(fake, out var cache);
        var coordinator = new PrefetchCoordinator(new TranslationService(new StubChatClientFactory(fake)), cache);
        cache.Set(
            new TranslationKey("doc-1", 6, new LanguageCode("en"), "test-model"),
            PageTranslation.Completed(6, new LanguageCode("en"), new LanguageCode("fr"), "test-model", "already", TranslationOrigin.OnDemand));

        coordinator.OnPageSettled(context, currentIndex: 5, ReadingDirection.Forward);
        await coordinator.PendingTask;

        Assert.True(cache.TryGet(new TranslationKey("doc-1", 6, new LanguageCode("en"), "test-model"), out var cached));
        Assert.Equal("already", cached.Text); // no overwrite of existing entry
        Assert.True(cache.TryGet(new TranslationKey("doc-1", 7, new LanguageCode("en"), "test-model"), out _)); // continued background work
    }
}
