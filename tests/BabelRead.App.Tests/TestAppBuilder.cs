using Avalonia;
using Avalonia.Headless;
using BabelRead.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace BabelRead.App.Tests;

/// <summary>Headless Avalonia app host for [AvaloniaFact] UI-loading smoke tests.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BabelRead.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
