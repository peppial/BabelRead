using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BabelRead.App.ViewModels;
using BabelRead.App.Views;
using BabelRead.Core.Documents;
using BabelRead.Core.Domain;
using BabelRead.Core.Preferences;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.App.Tests;

/// <summary>Reader shortcuts driven through real key events: arrows page the document and Ctrl+/Ctrl-
/// zoom the reading font, from anywhere in the reader window, whatever holds focus.</summary>
public sealed class ReaderViewKeyboardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-keys").FullName;

    [AvaloniaFact]
    public async Task Right_arrow_goes_to_the_next_page_and_left_arrow_goes_back()
    {
        var (window, vm) = await OpenReaderAsync("First page text", "Second page text");
        var firstPage = vm.VisiblePageText;

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await WaitForPageAsync(vm, 2);
        Assert.NotEqual(firstPage, vm.VisiblePageText); // a different slice of the flow is on screen

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        await WaitForPageAsync(vm, 1);
        Assert.Equal(firstPage, vm.VisiblePageText); // back to exactly the opening page
    }

    [AvaloniaFact]
    public async Task Right_arrow_pages_even_when_a_toolbar_button_has_focus()
    {
        var (window, vm) = await OpenReaderAsync("First page text", "Second page text");
        var view = (ReaderView)window.Content!;
        view.FindControl<Button>("OpenButton")!.Focus();

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);

        await WaitForPageAsync(vm, 2);
    }

    [AvaloniaFact]
    public async Task Arrows_at_the_document_edges_do_nothing()
    {
        var (window, vm) = await OpenReaderAsync("First page text", "Second page text");

        // Left at the very first visual page does nothing.
        Assert.False(vm.CanGoPrevious);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        await WaitForPageAsync(vm, 1);

        // Right advances one visual page.
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await WaitForPageAsync(vm, 2);

        // At the very last visual page, Right does nothing.
        await vm.JumpToPageAsync(vm.PageCount);
        var last = vm.PageCount;
        await WaitForPageAsync(vm, last);
        Assert.False(vm.CanGoNext);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await WaitForPageAsync(vm, last);
    }

    [AvaloniaFact]
    public async Task Ctrl_plus_grows_the_reading_font_and_ctrl_minus_shrinks_it()
    {
        var (window, vm) = await OpenReaderAsync("First page text", "Second page text");
        var original = vm.ReadingFontSize;

        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(original + ReadingFontSizes.Step, vm.ReadingFontSize);

        window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.Control);
        Assert.Equal(original - ReadingFontSizes.Step, vm.ReadingFontSize);

        // Line height tracks the font so the pane stays legible.
        Assert.Equal(Math.Round(vm.ReadingFontSize * 1.45), vm.ReadingLineHeight);
    }

    [AvaloniaFact]
    public async Task Font_zoom_stops_at_its_bounds()
    {
        var (window, vm) = await OpenReaderAsync("First page text", "Second page text");

        for (var i = 0; i < 40; i++)
        {
            window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.Control);
        }

        Assert.Equal(ReadingFontSizes.Minimum, vm.ReadingFontSize);

        for (var i = 0; i < 40; i++)
        {
            window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        }

        Assert.Equal(ReadingFontSizes.Maximum, vm.ReadingFontSize);
    }

    [AvaloniaFact]
    public async Task An_overflowing_page_is_clipped_to_the_viewport_with_no_scrollbar()
    {
        // Page-by-page reading replaced scrolling: a Core page longer than the window is cut into several
        // viewport-sized visual pages, and the reading surface is a clipped Panel — there is no ScrollViewer.
        var (window, vm) = await OpenReaderAsync("First page text", "Second page text");
        var view = (ReaderView)window.Content!;

        Assert.Null(view.FindControl<ScrollViewer>("ReadingScroll")); // the scroller is gone
        var surface = view.FindControl<Panel>("ReadingSurface")!;
        Assert.True(surface.ClipToBounds);

        // A padded page spans many visual pages, so the on-screen slice is a strict prefix of the whole flow.
        Assert.True(vm.PageCount > 1, "a long page must be cut into several visual pages");
        Assert.True(
            vm.VisiblePageText!.Length < vm.DisplayText!.Length,
            "one visual page must show less than the whole document flow");
    }

    private async Task<(Window Window, ReaderViewModel ViewModel)> OpenReaderAsync(params string[] pages)
    {
        var store = new InMemoryTranslationStore();
        var vm = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(new FakeChatClient()), store),
            store,
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(_dir, "prefs.json")));

        var window = new Window { Content = new ReaderView { DataContext = vm }, Width = 800, Height = 600 };
        window.Show();

        await vm.OpenAsync(SampleDocuments.CreatePdf(Path.Combine(_dir, $"{Guid.NewGuid():n}.pdf"), pages));

        // The view pushes reading metrics on a debounced reflow after layout; until they land the document
        // has no visual pages. Wait for the first page to be counted before driving keys.
        await WaitForPaginationAsync(vm);
        Assert.Equal(1, vm.PageNumber);
        return (window, vm);
    }

    /// <summary>Wait until the view has reported its size and the document has been sliced into visual pages.</summary>
    private static async Task WaitForPaginationAsync(ReaderViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (vm.PageNumber < 1 || vm.State == ReaderState.Loading))
        {
            await Task.Delay(20);
        }
    }

    /// <summary>The key handler is async void, so wait for the navigation it kicked off to settle.</summary>
    private static async Task WaitForPageAsync(ReaderViewModel vm, int expectedPageNumber)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (vm.PageNumber != expectedPageNumber || vm.State == ReaderState.Loading))
        {
            await Task.Delay(20);
        }

        Assert.Equal(expectedPageNumber, vm.PageNumber);
        Assert.Equal(ReaderState.Content, vm.State);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
