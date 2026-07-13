using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await WaitForPageAsync(vm, 2);
        Assert.Contains("Second", vm.OriginalText!, StringComparison.Ordinal);

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        await WaitForPageAsync(vm, 1);
        Assert.Contains("First", vm.OriginalText!, StringComparison.Ordinal);
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

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        await WaitForPageAsync(vm, 1);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await WaitForPageAsync(vm, 2);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await WaitForPageAsync(vm, 2);
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

    private async Task<(Window Window, ReaderViewModel ViewModel)> OpenReaderAsync(params string[] pages)
    {
        var vm = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(new FakeChatClient())),
            new TranslationCache(),
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(_dir, "prefs.json")));

        var window = new Window { Content = new ReaderView { DataContext = vm } };
        window.Show();

        await vm.OpenAsync(SampleDocuments.CreatePdf(Path.Combine(_dir, $"{Guid.NewGuid():n}.pdf"), pages));
        Assert.Equal(1, vm.PageNumber);
        return (window, vm);
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
