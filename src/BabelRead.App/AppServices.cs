using BabelRead.App.ViewModels;
using BabelRead.Core.Documents;
using BabelRead.Core.Models;
using BabelRead.Core.Preferences;
using BabelRead.Core.Translation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BabelRead.App;

/// <summary>
/// Builds the application's dependency-injection container. Reader/translation services are added by
/// later phases; this wires the foundational services, logging, and error handling (T013).
/// </summary>
internal static class AppServices
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Console as well as Debug: background failures (a prefetch that dies, a persist that throws) are
        // invisible otherwise, and they are exactly the ones that stop translation without a trace.
        services.AddLogging(b => b
            .AddDebug()
            .AddConsole()
            .SetMinimumLevel(Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("BABELREAD_LOGLEVEL"), true, out var level)
                ? level
                : LogLevel.Information));

        // Secure credential storage — native Keychain on macOS, in-memory elsewhere until a native
        // DPAPI/libsecret backend is wired for those platforms.
        if (MacOsKeychainSecretStore.IsSupported)
        {
            services.AddSingleton<ISecretStore, MacOsKeychainSecretStore>();
        }
        else
        {
            services.AddSingleton<ISecretStore, InMemorySecretStore>();
        }

        services.AddSingleton<IPreferencesStore>(_ => new JsonPreferencesStore());
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();

        // Document readers (stateful for the currently-open document → singletons) + registry.
        services.AddSingleton<IDocumentReader, PdfDocumentReader>();
        services.AddSingleton<IDocumentReader, EpubDocumentReader>();
        services.AddSingleton<DocumentReaderRegistry>();

        // Translation pipeline.
        services.AddSingleton<ITranslationStore, JsonTranslationStore>();
        services.AddSingleton<ITranslationService, TranslationService>();
        services.AddSingleton<IPrefetchCoordinator, PrefetchCoordinator>();

        services.AddSingleton<ReaderViewModel>();

        // Model configuration (US2).
        services.AddSingleton<IOllamaModelCatalog, OllamaModelCatalog>();
        services.AddSingleton<ModelProfileService>();
        services.AddSingleton<SettingsViewModel>();

        return services.BuildServiceProvider();
    }
}
