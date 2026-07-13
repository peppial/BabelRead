using BabelRead.App.ViewModels;
using BabelRead.Core.Documents;
using BabelRead.Core.Preferences;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.App.Tests;

public sealed class ReaderViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-vm").FullName;
    private readonly FakeChatClient _fake = new();

    private ReaderViewModel CreateViewModel(IPrefetchCoordinator? prefetch = null)
    {
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader(), new EpubDocumentReader() });
        var translation = new TranslationService(new StubChatClientFactory(_fake));
        return new ReaderViewModel(
            registry,
            translation,
            new TranslationCache(),
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
    public async Task Manually_opening_a_book_starts_from_the_first_page()
    {
        var path = CreatePdf("p1", "p2", "p3");
        var first = CreateViewModel();
        await first.OpenAsync(path);
        await first.NextPageAsync();
        await first.NextPageAsync();
        Assert.Equal(3, first.PageNumber);

        var reopenedManually = CreateViewModel();
        await reopenedManually.OpenAsync(path);

        Assert.Equal(1, reopenedManually.PageNumber);
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
        var cache = new TranslationCache();
        var translation = new TranslationService(new StubChatClientFactory(_fake));
        var prefetch = new PrefetchCoordinator(translation, cache);
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader(), new EpubDocumentReader() });
        var vm = new ReaderViewModel(registry, translation, cache, prefetch, prefs);

        await vm.OpenAsync(path);
        await prefetch.PendingTask;
        await Task.Delay(25); // allow UI-thread progress update from cache event

        Assert.Equal(3, vm.TranslatedPages);
        Assert.Equal(100d, vm.TranslationProgressPercent);
    }

    [Fact]
    public async Task Background_prefetched_translations_are_persisted_and_reused_after_reopen()
    {
        var path = CreatePdf("one", "two", "three");
        var prefs = new JsonPreferencesStore(Path.Combine(_dir, "persist-prefetch-prefs.json"));
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader(), new EpubDocumentReader() });

        var cache1 = new TranslationCache();
        var translation1 = new TranslationService(new StubChatClientFactory(_fake));
        var prefetch1 = new PrefetchCoordinator(translation1, cache1);
        var first = new ReaderViewModel(registry, translation1, cache1, prefetch1, prefs);
        await first.OpenAsync(path);
        await prefetch1.PendingTask;
        var callsAfterPrefetch = _fake.CallCount;

        var cache2 = new TranslationCache();
        var translation2 = new TranslationService(new StubChatClientFactory(_fake));
        var second = new ReaderViewModel(registry, translation2, cache2, new NoOpPrefetchCoordinator(), prefs);
        await second.OpenAsync(path);
        await second.NextPageAsync();

        Assert.Equal(callsAfterPrefetch, _fake.CallCount);
        Assert.Contains("two", second.TranslationText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Translation_percent_label_rounds_up_when_some_pages_are_translated()
    {
        var vm = CreateViewModel();
        vm.PageCount = 318;
        vm.TranslatedPages = 1;

        Assert.Equal("1% translated", vm.TranslationPercentLabel);
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
