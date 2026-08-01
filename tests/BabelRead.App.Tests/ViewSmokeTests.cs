using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using BabelRead.App.Controls;
using BabelRead.App.ViewModels;
using BabelRead.App.Views;
using BabelRead.Core.Documents;
using BabelRead.Core.Models;
using BabelRead.Core.Preferences;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.App.Tests;

/// <summary>Runtime XAML-loading smoke tests: prove the views and controls actually load and bind
/// against their view-models under a headless Avalonia platform (catches XAML errors the compiler
/// does not).</summary>
public class ViewSmokeTests
{
    [AvaloniaFact]
    public void StatePanel_loads_and_sets_message()
    {
        var panel = new StatePanel { Message = "Hello", ShowRetry = true };
        Assert.Equal("Hello", panel.Message);
        Assert.True(panel.ShowRetry);
    }

    [AvaloniaFact]
    public void ReaderView_loads_and_binds_to_its_view_model()
    {
        var store = new InMemoryTranslationStore();
        var vm = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(new FakeChatClient()), store),
            store,
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(Path.GetTempPath(), $"{System.Guid.NewGuid():n}.json")));

        var view = new ReaderView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        Assert.Same(vm, view.DataContext);
    }

    [AvaloniaFact]
    public void SettingsView_keeps_its_scrollbar_visible_so_the_bottom_controls_are_reachable()
    {
        var dir = Directory.CreateTempSubdirectory("babelread-settings-smoke").FullName;
        var prefs = new JsonPreferencesStore(Path.Combine(dir, "prefs.json"));
        var profiles = new ModelProfileService(prefs, new InMemorySecretStore(), new EmptyOllamaCatalog());
        var storeStub = new InMemoryTranslationStore();
        var reader = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(new FakeChatClient()), storeStub),
            storeStub,
            new NoOpPrefetchCoordinator(),
            prefs);

        var view = new SettingsView { DataContext = new SettingsViewModel(profiles, reader) };
        // A short window that forces the expanded form to overflow, like the one that hid the button.
        var window = new Window { Content = view, Width = 620, Height = 640 };
        window.Show();

        // The cloud-model form is collapsed by default, keeping the everyday settings above the fold.
        var expander = view.GetVisualDescendants().OfType<Expander>().Single();
        Assert.False(expander.IsExpanded);

        expander.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.False(scroll.AllowAutoHide);

        // The scrollable extent must cover the whole content — ScrollViewer padding used to be left out of
        // it, so the bottom controls fell into an unreachable dead zone.
        var content = (Control)scroll.Content!;
        Assert.True(scroll.Extent.Height >= content.Bounds.Height, "content must be fully within the scroll extent");

        // Scrolled to the bottom, the Add button is entirely inside the viewport (top and bottom both visible).
        scroll.Offset = scroll.Offset.WithY(scroll.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        var button = view.FindControl<Button>("AddCloudButton")!;
        var top = button.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
        var bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), scroll)!.Value.Y;
        Assert.True(top >= 0 && bottom <= scroll.Viewport.Height + 1, $"Add button must be fully visible when scrolled to the bottom (top={top}, bottom={bottom}, viewport={scroll.Viewport.Height})");
    }

    [AvaloniaFact]
    public void A_short_page_is_vertically_centred_in_the_reading_window()
    {
        var store = new InMemoryTranslationStore();
        var vm = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(new FakeChatClient()), store),
            store,
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(Path.GetTempPath(), $"{System.Guid.NewGuid():n}.json")));
        vm.ShowingTranslation = false;
        vm.OriginalText = "A short closing line at the end of a chapter.";
        vm.State = ReaderState.Content;

        var view = new ReaderView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = view.FindControl<ScrollViewer>("ReadingScroll")!;
        var text = view.FindControl<SelectableTextBlock>("PageText")!;

        // A short page must not scroll, and it should sit near the vertical middle rather than pinned to the
        // top with a tall empty gap below it.
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1, "a short page must not scroll");
        var textCenterY = text.TranslatePoint(new Point(0, text.Bounds.Height / 2), scroll)!.Value.Y;
        var viewportCenterY = scroll.Viewport.Height / 2;
        Assert.True(
            Math.Abs(textCenterY - viewportCenterY) < 90,
            $"short page should be vertically centred: text centre {textCenterY}, viewport centre {viewportCenterY}");
    }

    private sealed class EmptyOllamaCatalog : IOllamaModelCatalog
    {
        public Task<IReadOnlyList<string>> ListAvailableModelsAsync(Uri endpoint, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
