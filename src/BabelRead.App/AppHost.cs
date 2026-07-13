using Avalonia.Controls;
using BabelRead.App.ViewModels;
using BabelRead.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BabelRead.App;

/// <summary>Composition root: builds the DI container, resolves the view-models, and hosts the reader
/// view in the main window (with a settings window on demand).</summary>
internal static class AppHost
{
    public static Window CreateMainWindow()
    {
        var services = AppServices.Build();
        var reader = services.GetRequiredService<ReaderViewModel>();
        var settings = services.GetRequiredService<SettingsViewModel>();

        // Load persisted preferences and model profiles before showing content (FR-012, T038).
        _ = InitializeAsync(reader, settings);

        var window = new MainWindow();
        var readerView = new ReaderView { DataContext = reader };
        readerView.OpenSettingsRequested += (_, _) =>
        {
            var settingsWindow = new SettingsWindow(settings);
            settingsWindow.Show(window);
        };

        window.FindControl<ContentControl>("RootContent")!.Content = readerView;
        return window;
    }

    private static async Task InitializeAsync(ReaderViewModel reader, SettingsViewModel settings)
    {
        await settings.LoadAsync().ConfigureAwait(false); // sets the active model first
        await reader.InitializeAsync().ConfigureAwait(false); // then restores prefs + last opened document
    }
}
