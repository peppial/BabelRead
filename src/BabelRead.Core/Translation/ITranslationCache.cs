using System.Diagnostics.CodeAnalysis;
using BabelRead.Core.Domain;

namespace BabelRead.Core.Translation;

/// <summary>Session-scoped store of produced translations, reused across on-demand and prefetch (FR-008).</summary>
public interface ITranslationCache
{
    event EventHandler<TranslationCachedEventArgs>? EntryStored;

    bool TryGet(TranslationKey key, [NotNullWhen(true)] out PageTranslation? value);

    void Set(TranslationKey key, PageTranslation value);

    int CountForDocument(string documentId, LanguageCode targetLanguage, string modelId);

    /// <summary>Clears all entries (e.g. when the document/session closes).</summary>
    void Clear();
}

public sealed class TranslationCachedEventArgs(TranslationKey key, PageTranslation value) : EventArgs
{
    public TranslationKey Key { get; } = key;

    public PageTranslation Value { get; } = value;
}
