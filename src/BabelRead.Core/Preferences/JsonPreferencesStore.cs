using System.Text.Json;
using System.Text.Json.Serialization;
using BabelRead.Core.Domain;

namespace BabelRead.Core.Preferences;

/// <summary>Serializes <see cref="LanguageCode"/> as its bare BCP-47 string.</summary>
internal sealed class LanguageCodeJsonConverter : JsonConverter<LanguageCode>
{
    public override LanguageCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, LanguageCode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Code);
}

/// <summary>Loads/saves <see cref="ReaderPreferences"/> as a JSON file under the per-user app-data
/// directory. Never stores secrets (FR-012). A missing file yields defaults.</summary>
public sealed class JsonPreferencesStore : IPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new LanguageCodeJsonConverter(), new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public JsonPreferencesStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "BabelRead");
        return Path.Combine(dir, "preferences.json");
    }

    public async Task<ReaderPreferences> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            return new ReaderPreferences();
        }

        await using var stream = File.OpenRead(_filePath);
        var prefs = await JsonSerializer.DeserializeAsync<ReaderPreferences>(stream, Options, ct).ConfigureAwait(false);
        return prefs ?? new ReaderPreferences();
    }

    public async Task SaveAsync(ReaderPreferences preferences, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, preferences, Options, ct).ConfigureAwait(false);
    }
}
