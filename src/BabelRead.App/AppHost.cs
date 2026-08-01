using Avalonia.Controls;
using BabelRead.App.ViewModels;
using BabelRead.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        _ = InitializeAsync(reader, settings, services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(AppHost)));

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

    /// <summary>
    /// Startup restore. It must stay on the UI thread — it drives view-model properties the views are
    /// bound to — so no ConfigureAwait(false) here. The task is fire-and-forget (the window has to come
    /// up first), which is exactly why it has to log its own failures: nobody else will observe them.
    /// </summary>
    private static async Task InitializeAsync(ReaderViewModel reader, SettingsViewModel settings, ILogger logger)
    {
        try
        {
            await settings.LoadAsync(); // sets the active model first
            await reader.InitializeAsync(); // then restores prefs + last opened document
        }
        catch (Exception ex)
        {
            reader.ShowStartupFailure(ex.Message);
            logger.LogError(ex, "Startup initialization failed.");
        }
    }
}
