namespace BabelRead.Core.Domain;

/// <summary>Which pane the original/translation toggle shows by default (FR-013).</summary>
public enum PaneView
{
    Original,
    Translation,
}

/// <summary>Persisted reader settings (JSON; secrets excluded) — spec entity: Reader Preferences.</summary>
public sealed class ReaderPreferences
{
    /// <summary>Reader-selected target language (FR-006).</summary>
    public LanguageCode TargetLanguage { get; set; } = new("en");

    /// <summary>Optional per-document source-language overrides, keyed by document id.</summary>
    public Dictionary<string, string> SourceLanguageOverrides { get; init; } = new();

    /// <summary>Currently selected model profile (FR-012).</summary>
    public string? ActiveModelProfileId { get; set; }

    /// <summary>Reader-configured model profiles (cloud + local). Secrets are not stored here — only a
    /// reference name into the secret store.</summary>
    public List<StoredModelProfile> ModelProfiles { get; init; } = new();

    /// <summary>Default view for the original/translation toggle.</summary>
    public PaneView PaneToggleDefault { get; set; } = PaneView.Translation;

    /// <summary>Most recently opened document path; used for startup restore.</summary>
    public string? LastOpenedDocumentPath { get; set; }

    /// <summary>Persisted completed translations keyed by document id.</summary>
    public Dictionary<string, List<StoredTranslation>> TranslationCacheByDocument { get; init; } = new();

    /// <summary>Last read page index per document id.</summary>
    public Dictionary<string, int> LastReadPageByDocument { get; init; } = new();

    /// <summary>Reading-pane font size in device-independent pixels (Ctrl+/Ctrl- zoom).</summary>
    public double ReadingFontSize { get; set; } = ReadingFontSizes.Default;
}

/// <summary>Bounds for the reading-pane font zoom.</summary>
public static class ReadingFontSizes
{
    public const double Default = 17;

    public const double Minimum = 11;

    public const double Maximum = 40;

    public const double Step = 2;

    /// <summary>Font size the EPUB pagination heuristic was tuned against; reflow scales relative to it.</summary>
    public const double PaginationBaseline = 22;

    public static double Clamp(double size) =>
        double.IsFinite(size) ? Math.Clamp(size, Minimum, Maximum) : Default;
}

/// <summary>Serializable projection of a <see cref="ModelProfile"/> (no secret material).</summary>
public sealed class StoredModelProfile
{
    public string ProfileId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ModelKind Kind { get; set; }

    public string ModelId { get; set; } = string.Empty;

    public string? Endpoint { get; set; }

    /// <summary>Name/reference of the credential in the secret store (cloud only); never the raw key.</summary>
    public string? CredentialName { get; set; }
}

/// <summary>Serializable projection of a completed translation for persistent per-book cache.</summary>
public sealed class StoredTranslation
{
    public int PageIndex { get; set; }

    public string TargetLanguage { get; set; } = string.Empty;

    public string SourceLanguage { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}
