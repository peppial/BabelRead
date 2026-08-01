using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Core.Tests.Translation;

public class PrefetchCoordinatorTests
{
    private static readonly ModelProfile Model = new("p1", "Local", ModelKind.Local, "test-model");
    private static readonly Document Document = new("doc-1", "Doc", "/tmp/d.pdf", DocumentFormat.Pdf, 20, new LanguageCode("fr"));

    /// <summary>Key of the single segment on the page the fake reader below returns for that index.</summary>
    private static TranslationKey Key(int index) =>
        TranslationKey.For($"page {index} text", new LanguageCode("fr"), new LanguageCode("en"), "test-model");

    private static PrefetchContext Context() =>
        new(
            Document,
            new LanguageCode("en"),
            SourceOverride: null,
            Model,
            GetPageAsync: (index, ct) => Task.FromResult<Page?>(new Page(index, $"page {index} text")));

    private static (PrefetchCoordinator Coordinator, InMemoryTranslationStore Store) Create(FakeChatClient fake)
    {
        var store = new InMemoryTranslationStore();
        var service = new TranslationService(new StubChatClientFactory(fake), store);
        var coordinator = new PrefetchCoordinator(service, store) { Mode = BackgroundTranslation.FullSpeed };
        return (coordinator, store);
    }

    [Fact]
    public async Task Prefetches_next_forward_page_into_the_store()
    {
        var (coordinator, store) = Create(new FakeChatClient());

        coordinator.OnPageSettled(Context(), currentIndex: 5, ReadingDirection.Forward);
        await coordinator.PendingTask;

        Assert.True(store.TryGet(Key(6), out _));
        Assert.True(store.TryGet(Key(19), out _)); // runs ahead in background
    }

    [Fact]
    public async Task On_the_last_page_it_translates_the_rest_of_the_book_instead_of_stopping()
    {
        var fake = new FakeChatClient();
        var (coordinator, store) = Create(fake);

        coordinator.OnPageSettled(Context(), currentIndex: 19, ReadingDirection.Forward); // last page (count 20)
        await coordinator.PendingTask;

        // Nothing lies ahead, so it fills in the pages the reader skipped past.
        Assert.True(store.TryGet(Key(0), out _));
        Assert.True(store.TryGet(Key(10), out _));
        Assert.Equal(20, fake.CallCount);
    }

    [Fact]
    public async Task Pages_behind_the_reader_are_translated_once_the_read_ahead_is_done()
    {
        var fake = new FakeChatClient();
        var (coordinator, store) = Create(fake);

        coordinator.OnPageSettled(Context(), currentIndex: 15, ReadingDirection.Forward);
        await coordinator.PendingTask;

        Assert.True(store.TryGet(Key(19), out _)); // ahead of the reader
        Assert.True(store.TryGet(Key(3), out _));  // behind the reader — would never have been done before
        Assert.Equal(20, fake.CallCount); // every page in the book
    }

    [Fact]
    public async Task Cancelling_stops_the_pending_prefetch_from_storing()
    {
        var (coordinator, store) = Create(new FakeChatClient(delay: TimeSpan.FromMilliseconds(500)));

        coordinator.OnPageSettled(Context(), currentIndex: 5, ReadingDirection.Forward);
        coordinator.CancelPending();
        await coordinator.PendingTask; // completes (cancelled) without throwing

        Assert.False(store.TryGet(Key(6), out _));
    }

    [Fact]
    public async Task Off_mode_translates_only_the_next_two_pages()
    {
        var fake = new FakeChatClient();
        var (coordinator, store) = Create(fake);
        coordinator.Mode = BackgroundTranslation.Off;

        coordinator.OnPageSettled(Context(), currentIndex: 5, ReadingDirection.Forward);
        await coordinator.PendingTask;

        Assert.True(store.TryGet(Key(6), out _)); // the next two pages, so turns stay instant
        Assert.True(store.TryGet(Key(7), out _));
        Assert.False(store.TryGet(Key(8), out _)); // and nothing further — no whole-book grind
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task Off_mode_does_not_run_ahead_past_the_last_page()
    {
        var fake = new FakeChatClient();
        var (coordinator, store) = Create(fake);
        coordinator.Mode = BackgroundTranslation.Off;

        coordinator.OnPageSettled(Context(), currentIndex: 18, ReadingDirection.Forward); // page count 20
        await coordinator.PendingTask;

        Assert.True(store.TryGet(Key(19), out _)); // only the one remaining page
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Switching_to_off_mid_grind_stops_the_whole_book_run()
    {
        // A slow model so the full-speed whole-book run is genuinely in flight when we switch to Off.
        var fake = new FakeChatClient(delay: TimeSpan.FromMilliseconds(40));
        var (coordinator, _) = Create(fake); // starts FullSpeed
        coordinator.OnPageSettled(Context(), currentIndex: 0, ReadingDirection.Forward); // grind pages 1..19
        await Task.Delay(TimeSpan.FromMilliseconds(60)); // let a page or two go through

        // Exactly what ReaderViewModel.SetBackgroundTranslationAsync(Off) does: flip the mode (which cancels
        // the in-flight grind) and reschedule under the new mode.
        coordinator.Mode = BackgroundTranslation.Off;
        coordinator.OnPageSettled(Context(), currentIndex: 0, ReadingDirection.Forward); // Off => at most 2 pages
        await coordinator.PendingTask;
        await Task.Delay(TimeSpan.FromMilliseconds(200)); // give any leaked old task time to keep going

        // The grind stopped: only the in-flight page plus the 2-page Off read-ahead, never the whole book.
        Assert.True(fake.CallCount < 6, $"Off should stop the whole-book grind, but {fake.CallCount} pages were translated.");
    }

    [Fact]
    public async Task Gentle_mode_pauses_between_pages()
    {
        var fake = new FakeChatClient();
        var (coordinator, _) = Create(fake);
        coordinator.Mode = BackgroundTranslation.Gentle; // 10s between pages

        coordinator.OnPageSettled(Context(), currentIndex: 5, ReadingDirection.Forward);
        await Task.Delay(TimeSpan.FromSeconds(1));
        coordinator.CancelPending();
        await coordinator.PendingTask;

        // One page, then it idles — where full speed would have burned through several by now.
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Skips_a_page_a_previous_session_already_translated()
    {
        var fake = new FakeChatClient();
        var (coordinator, store) = Create(fake);
        await store.SaveAsync(Key(6), "already");

        coordinator.OnPageSettled(Context(), currentIndex: 5, ReadingDirection.Forward);
        await coordinator.PendingTask;

        Assert.True(store.TryGet(Key(6), out var kept));
        Assert.Equal("already", kept); // not overwritten
        Assert.True(store.TryGet(Key(7), out _)); // work continued past it
        Assert.Equal(19, fake.CallCount); // 20 pages, and the one already translated cost nothing
    }
}
