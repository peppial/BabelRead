using BabelRead.App.ViewModels;
using BabelRead.Core.Documents;
using BabelRead.Core.Domain;
using BabelRead.Core.Preferences;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.App.Tests;

public sealed class ReaderViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-vm").FullName;
    private readonly FakeChatClient _fake = new();
    private readonly InMemoryTranslationStore _store = new();

    private ReaderViewModel CreateViewModel(IPrefetchCoordinator? prefetch = null)
    {
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader(), new EpubDocumentReader() });
        var translation = new TranslationService(new StubChatClientFactory(_fake), _store);
        return new ReaderViewModel(
            registry,
            translation,
            _store,
            prefetch ?? new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(_dir, "prefs.json")));
    }

    private string CreatePdf(params string[] pages) => SampleDocuments.CreatePdf(Path.Combine(_dir, $"{Guid.NewGuid():n}.pdf"), pages);

    [Fact]
    public async Task Opening_a_document_translates_the_first_page()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf("Bonjour le monde", "Deuxieme page"));

        Assert.Equal(ReaderState.Content, vm.State);
        Assert.Equal(2, vm.PageCount);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal("Page 1/2", vm.CurrentPageLabel);
        Assert.Contains("Bonjour", vm.OriginalText!, StringComparison.Ordinal);
        Assert.Contains("Bonjour", vm.TranslationText!, StringComparison.Ordinal); // fake echoes original
    }

    [Fact]
    public async Task Navigating_next_updates_page_and_translation_in_sync()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf("First page text", "Second page text"));

        await vm.NextPageAsync();

        Assert.Equal(2, vm.PageNumber);
        Assert.Equal("Page 2/2", vm.CurrentPageLabel);
        Assert.Contains("Second", vm.OriginalText!, StringComparison.Ordinal);
        Assert.Contains("Second", vm.TranslationText!, StringComparison.Ordinal);
        Assert.True(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public async Task Off_mode_translates_at_most_the_current_page_and_two_ahead_while_sitting_still()
    {
        // Real coordinator, not the no-op: this is the actual background-translation path the user runs.
        var translation = new TranslationService(new StubChatClientFactory(_fake), _store);
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader(), new EpubDocumentReader() });
        var coordinator = new PrefetchCoordinator(translation, _store);
        var vm = new ReaderViewModel(registry, translation, _store, coordinator,
            new JsonPreferencesStore(Path.Combine(_dir, "prefs.json")));

        await vm.SetBackgroundTranslationAsync(BackgroundTranslation.Off);
        // A 10-page book (CreatePdf gives one reader page per entry).
        var pages = Enumerable.Range(0, 10).Select(i => $"Chapter content number {i}").ToArray();
        await vm.OpenAsync(CreatePdf(pages));

        // Sit still: let every scheduled prefetch finish.
        await coordinator.PendingTask;
        await Task.Delay(200);
        await coordinator.PendingTask;

        // Current page + 2 ahead = 3 pages of work, never the whole 10-page book.
        Assert.True(_fake.CallCount <= 4, $"Off should stop after the page + 2 ahead, but {_fake.CallCount} pages were translated.");
    }

    [Fact]
    public async Task Off_mode_stays_bounded_even_when_the_view_reflows_repeatedly()
    {
        var translation = new TranslationService(new StubChatClientFactory(_fake), _store);
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader(), new EpubDocumentReader() });
        var coordinator = new PrefetchCoordinator(translation, _store);
        var vm = new ReaderViewModel(registry, translation, _store, coordinator,
            new JsonPreferencesStore(Path.Combine(_dir, "prefs.json")));

        await vm.SetBackgroundTranslationAsync(BackgroundTranslation.Off);
        var pages = Enumerable.Range(0, 12).Select(i => $"Chapter content number {i}").ToArray();
        await vm.OpenAsync(CreatePdf(pages));

        // Simulate the view settling its layout — the exact call ReaderView.ScheduleReflow makes, several
        // times at different sizes, as the window and toolbar settle. Each re-opens and reschedules prefetch.
        for (var i = 0; i < 6; i++)
        {
            await vm.ReflowForViewportAsync(900 - (i * 5), 1100 - (i * 5));
            await coordinator.PendingTask;
        }

        await Task.Delay(200);
        await coordinator.PendingTask;

        // Every reflow moves the reader a little, but Off must never let the total creep toward the whole book.
        Assert.True(_fake.CallCount <= 6, $"Off crept to {_fake.CallCount} pages across reflows — it should stay near the page + 2 ahead.");
    }

    [Fact]
    public async Task Toggle_flips_between_original_and_translation()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf("Hello"));
        var before = vm.ShowingTranslation;

        vm.ToggleView();

        Assert.NotEqual(before, vm.ShowingTranslation);
    }

    [Fact]
    public async Task Text_less_page_shows_the_no_text_state()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf(""));

        Assert.Equal(ReaderState.NoText, vm.State);
        Assert.Null(vm.TranslationText);
    }

    [Fact]
    public async Task Revisiting_a_page_reuses_the_cached_translation()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf("Page one", "Page two"));
        await vm.NextPageAsync();      // page 2 → call 2
        await vm.PreviousPageAsync();  // page 1 → cache hit, no new call

        Assert.Equal(2, _fake.CallCount);
        Assert.Equal(1, vm.PageNumber);
        Assert.Equal(ReaderState.Content, vm.State);
    }

    [Fact]
    public async Task Translation_stored_by_a_previous_session_is_reused_without_calling_the_model()
    {
        var path = CreatePdf("Bonjour", "Bonsoir");
        var first = CreateViewModel();
        await first.OpenAsync(path);
        Assert.Equal(1, _fake.CallCount);

        var reopened = CreateViewModel();
        await reopened.InitializeAsync();

        Assert.Equal(ReaderState.Content, reopened.State);
        Assert.Equal(1, _fake.CallCount); // served from the persisted cache
    }

    [Fact]
    public async Task Repaginating_the_book_does_not_lose_a_single_translation()
    {
        // The whole point of translating segments rather than pages: re-cutting the book into different
        // pages regroups segments, so nothing has to be translated twice.
        var epub = SampleDocuments.CreateEpub(
            Path.Combine(_dir, "book.epub"),
            "Book",
            "fr",
            string.Join("", Enumerable.Range(0, 30).Select(i => $"<p>Paragraphe numero {i} avec du texte.</p>")));

        var vm = CreateViewModel();
        await vm.OpenAsync(epub);

        // Translate the whole book at the initial pagination.
        while (vm.CanGoNext)
        {
            await vm.NextPageAsync();
        }

        var callsBefore = _fake.CallCount;
        var segmentsBefore = vm.TranslatedSegments;
        Assert.True(segmentsBefore > 0);

        // Repaginate hard (a much smaller reading surface → many more, smaller pages).
        await vm.ReflowForViewportAsync(400, 300);
        await vm.JumpToPageAsync(1);
        while (vm.CanGoNext)
        {
            await vm.NextPageAsync();
        }

        Assert.Equal(callsBefore, _fake.CallCount); // not one segment re-translated
        Assert.Equal(segmentsBefore, vm.TranslatedSegments);
    }

    [Fact]
    public async Task While_a_page_translates_the_original_text_is_shown_instead_of_a_blank_pane()
    {
        var slow = new FakeChatClient(delay: TimeSpan.FromSeconds(2));
        var store = new InMemoryTranslationStore();
        var vm = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(slow), store),
            store,
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(_dir, "prefs.json")));

        await vm.OpenAsync(CreatePdf("First page text", "Second page text"));
        Assert.Equal(ReaderState.Content, vm.State); // page 1 translated

        var turning = vm.NextPageAsync(); // page 2 is not translated yet
        var deadline = DateTime.UtcNow.AddSeconds(1); // page text is extracted long before the model replies
        while (DateTime.UtcNow < deadline && vm.OriginalText?.Contains("Second", StringComparison.Ordinal) != true)
        {
            await Task.Delay(10);
        }

        // Mid-translation the reader must show page 2's own source text, not a blank pane and not
        // page 1's translation.
        Assert.Equal(ReaderState.Loading, vm.State);
        Assert.Contains("Second", vm.DisplayText!, StringComparison.Ordinal);
        Assert.True(vm.IsContentVisible);
        Assert.True(vm.IsTranslatingFallbackVisible);
        Assert.False(vm.IsStatusVisible);

        await turning;
        Assert.Contains("Second", vm.TranslationText!, StringComparison.Ordinal);
        Assert.False(vm.IsTranslatingFallbackVisible);
    }

    [Fact]
    public async Task Translation_arriving_from_prefetch_for_the_current_page_is_shown()
    {
        // A page whose segments are already in the store must render from the store, with no model call.
        var slow = new FakeChatClient(delay: TimeSpan.FromSeconds(30)); // a model call would blow the test's time budget
        var store = new InMemoryTranslationStore();
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() });
        var vm = new ReaderViewModel(
            registry,
            new TranslationService(new StubChatClientFactory(slow), store),
            store,
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(_dir, "prefs.json")));

        var path = CreatePdf("Bonjour", "Bonsoir");
        _ = vm.OpenAsync(path); // page 1 starts translating (slowly)
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && vm.OriginalText is null)
        {
            await Task.Delay(10);
        }

        Assert.Equal(ReaderState.Loading, vm.State); // the slow model is still working on page 1

        // A previous session had already translated page 2's segments.
        var reader = registry.ResolveFor(path);
        var doc = await reader.OpenAsync(path, TestContext.Current.CancellationToken);
        var page1 = await reader.GetPageAsync(doc, 1, TestContext.Current.CancellationToken);
        foreach (var segment in page1.Segments)
        {
            await store.SaveAsync(
                TranslationKey.For(segment, doc.DetectedSourceLanguage, vm.TargetLanguage, vm.ActiveModel.ModelId),
                "PREFETCHED",
                TestContext.Current.CancellationToken);
        }

        // Turning to it renders from the store immediately — the 30s model call is never made.
        await vm.NextPageAsync();

        Assert.Equal("PREFETCHED", vm.TranslationText);
        Assert.Contains("PREFETCHED", vm.DisplayText!, StringComparison.Ordinal); // page 2's segment, folded into the flow
        Assert.Equal(ReaderState.Content, vm.State);
    }

    [Fact]
    public async Task Turning_to_an_already_translated_page_never_flashes_the_translating_state()
    {
        var vm = CreateViewModel();
        await vm.OpenAsync(CreatePdf("First page text", "Second page text"));
        await vm.NextPageAsync(); // page 2 translated and cached

        var states = new List<ReaderState>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReaderViewModel.State))
            {
                states.Add(vm.State);
            }
        };

        await vm.PreviousPageAsync(); // page 1 is already translated — must appear instantly

        Assert.DoesNotContain(ReaderState.Loading, states);
        Assert.Equal(ReaderState.Content, vm.State);
        Assert.Contains("First", vm.TranslationText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Next_page_stays_clickable_while_the_current_page_is_still_translating()
    {
        var slow = new FakeChatClient(delay: TimeSpan.FromSeconds(2));
        var store = new InMemoryTranslationStore();
        var vm = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(slow), store),
            store,
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(_dir, "prefs.json")));

        await vm.OpenAsync(CreatePdf("One", "Two", "Three"));

        vm.NextPageCommand.Execute(null); // the toolbar button's path — page 2 translates slowly
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline && vm.State != ReaderState.Loading)
        {
            await Task.Delay(10);
        }

        // The reader must be able to keep turning pages instead of being pinned to a translating page.
        Assert.True(vm.CanGoNext);
        Assert.True(vm.NextPageCommand.CanExecute(null));
        Assert.True(vm.PreviousPageCommand.CanExecute(null));

        await vm.NextPageCommand.ExecutionTask!;
    }

    [Fact]
    public async Task Opening_an_unsupported_file_shows_an_error()
    {
        var vm = CreateViewModel();
        var bad = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(bad, "hi");

        await vm.OpenAsync(bad);

        Assert.Equal(ReaderState.Error, vm.State);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public async Task Initialize_auto_opens_the_last_opened_document()
    {
        var path = CreatePdf("Autoload page");
        var first = CreateViewModel();
        await first.OpenAsync(path);

        var reopened = CreateViewModel();
        await reopened.InitializeAsync();

        Assert.Equal(ReaderState.Content, reopened.State);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), reopened.Title);
        Assert.Equal(1, reopened.PageNumber);
    }

    [Fact]
    public async Task Initialize_restores_last_read_page_of_last_opened_document()
    {
        var path = CreatePdf("p1", "p2", "p3");
        var first = CreateViewModel();
        await first.OpenAsync(path);
        await first.NextPageAsync();
        await first.NextPageAsync();
        Assert.Equal(3, first.PageNumber);

        var reopened = CreateViewModel();
        await reopened.InitializeAsync();

        Assert.Equal(ReaderState.Content, reopened.State);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), reopened.Title);
        Assert.Equal(3, reopened.PageNumber);
    }

    [Fact]
    public async Task Manually_reopening_a_book_resumes_where_it_was_left_off()
    {
        var path = CreatePdf("p1", "p2", "p3");
        var first = CreateViewModel();
        await first.OpenAsync(path);
        await first.NextPageAsync();
        await first.NextPageAsync();
        Assert.Equal(3, first.PageNumber);

        var reopenedManually = CreateViewModel();
        await reopenedManually.OpenAsync(path);

        Assert.Equal(3, reopenedManually.PageNumber); // resumed at the last read page, not page 1
    }

    [Fact]
    public async Task Each_book_keeps_its_own_reading_position()
    {
        var bookA = CreatePdf("a1", "a2", "a3", "a4");
        var bookB = CreatePdf("b1", "b2", "b3");
        var vm = CreateViewModel();

        await vm.OpenAsync(bookA);
        await vm.NextPageAsync();
        await vm.NextPageAsync();
        Assert.Equal(3, vm.PageNumber); // book A on page 3

        await vm.OpenAsync(bookB); // a book never opened before starts at page 1
        Assert.Equal(1, vm.PageNumber);

        await vm.OpenAsync(bookA); // back to A, still on its own page 3
        Assert.Equal(3, vm.PageNumber);
    }

    [Fact]
    public async Task Persisted_translation_cache_is_reused_after_restart_for_same_book()
    {
        var path = CreatePdf("Persisted text");
        var first = CreateViewModel();
        await first.OpenAsync(path);
        var callsAfterFirstSession = _fake.CallCount;

        var second = CreateViewModel();
        await second.OpenAsync(path);

        Assert.Equal(callsAfterFirstSession, _fake.CallCount);
        Assert.Equal(ReaderState.Content, second.State);
        Assert.Contains("Persisted", second.TranslationText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Background_prefetch_updates_translation_progress_indicator()
    {
        var path = CreatePdf("one", "two", "three");
        var prefs = new JsonPreferencesStore(Path.Combine(_dir, "progress-prefs.json"));
        var store = new InMemoryTranslationStore();
        var translation = new TranslationService(new StubChatClientFactory(_fake), store);
        var prefetch = new PrefetchCoordinator(translation, store);
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader(), new EpubDocumentReader() });
        var vm = new ReaderViewModel(registry, translation, store, prefetch, prefs);

        await vm.OpenAsync(path);
        await prefetch.PendingTask;
        await Task.Delay(25); // allow the progress update raised by the store

        Assert.Equal(3, vm.TranslatedSegments); // one paragraph per page
        Assert.Equal(100d, vm.TranslationProgressPercent);
    }

    [Fact]
    public async Task Background_prefetched_translations_are_persisted_and_reused_after_reopen()
    {
        var path = CreatePdf("one", "two", "three");
        var prefs = new JsonPreferencesStore(Path.Combine(_dir, "persist-prefetch-prefs.json"));
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader(), new EpubDocumentReader() });
        var translationsDir = Path.Combine(_dir, "translations");

        // A first session translates and prefetches, writing segments to disk.
        var store1 = new JsonTranslationStore(translationsDir);
        var translation1 = new TranslationService(new StubChatClientFactory(_fake), store1);
        var prefetch1 = new PrefetchCoordinator(translation1, store1);
        var first = new ReaderViewModel(registry, translation1, store1, prefetch1, prefs);
        await first.OpenAsync(path);
        await prefetch1.PendingTask;
        var callsAfterPrefetch = _fake.CallCount;
        await store1.FlushAsync();

        // A second session — brand new store instance, reading only what is on disk.
        var store2 = new JsonTranslationStore(translationsDir);
        var translation2 = new TranslationService(new StubChatClientFactory(_fake), store2);
        var second = new ReaderViewModel(registry, translation2, store2, new NoOpPrefetchCoordinator(), prefs);
        await second.OpenAsync(path);
        await second.NextPageAsync();

        Assert.Equal(callsAfterPrefetch, _fake.CallCount); // nothing re-translated
        Assert.Contains("two", second.TranslationText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Translation_percent_label_rounds_up_and_shows_the_segment_counts()
    {
        var vm = CreateViewModel();
        vm.TotalSegments = 2107;
        vm.TranslatedSegments = 6;
        vm.TranslationProgressPercent = 0.3;

        // Rounded up so "some work done" never reads as 0%, with the counts behind it: one segment is a
        // twentieth of a percent, so the percentage alone looks stuck.
        Assert.Equal("1% translated (6/2107)", vm.TranslationPercentLabel);
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
