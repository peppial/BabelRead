using BabelRead.Core.Domain;

namespace BabelRead.Core.Translation;

/// <summary>
/// Resolves the effective source language for a document: a reader-supplied per-document override wins
/// over the auto-detected language, which wins over "unknown" (FR-006). Also reads/writes the override
/// in <see cref="ReaderPreferences"/>.
/// </summary>
public static class LanguageResolver
{
    /// <summary>Effective source language: override ?? detected.</summary>
    public static LanguageCode ResolveSource(Document document, LanguageCode? overrideForDocument)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (overrideForDocument is { IsUnknown: false } o)
        {
            return o;
        }

        return document.DetectedSourceLanguage;
    }

    /// <summary>Returns the stored source-language override for a document, if any.</summary>
    public static LanguageCode? GetOverride(ReaderPreferences preferences, string documentId) =>
        preferences.SourceLanguageOverrides.TryGetValue(documentId, out var code) && !string.IsNullOrWhiteSpace(code)
            ? new LanguageCode(code)
            : null;

    /// <summary>Stores (or clears) the source-language override for a document.</summary>
    public static void SetOverride(ReaderPreferences preferences, string documentId, LanguageCode? source)
    {
        if (source is { IsUnknown: false } s)
        {
            preferences.SourceLanguageOverrides[documentId] = s.Code;
        }
        else
        {
            preferences.SourceLanguageOverrides.Remove(documentId);
        }
    }
}
