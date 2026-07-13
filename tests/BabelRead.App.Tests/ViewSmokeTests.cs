using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BabelRead.App.Controls;
using BabelRead.App.ViewModels;
using BabelRead.App.Views;
using BabelRead.Core.Documents;
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
        var vm = new ReaderViewModel(
            new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() }),
            new TranslationService(new StubChatClientFactory(new FakeChatClient())),
            new TranslationCache(),
            new NoOpPrefetchCoordinator(),
            new JsonPreferencesStore(Path.Combine(Path.GetTempPath(), $"{System.Guid.NewGuid():n}.json")));

        var view = new ReaderView { DataContext = vm };
        var window = new Window { Content = view };
        window.Show();

        Assert.Same(vm, view.DataContext);
    }
}
