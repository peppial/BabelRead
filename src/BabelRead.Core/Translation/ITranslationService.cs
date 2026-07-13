using BabelRead.Core.Domain;

namespace BabelRead.Core.Translation;

/// <summary>Produces one page's translation. Provider-agnostic — depends only on the active model client.</summary>
public interface ITranslationService
{
    /// <summary>
    /// Translates <paramref name="page"/> into <paramref name="target"/>. Returns a
    /// <see cref="TranslationStatus.Completed"/> result on success, <see cref="TranslationStatus.Failed"/>
    /// on model/network error; short-circuits when source == target. The result's
    /// <see cref="Translation.PageIndex"/> always equals <see cref="Page.Index"/> (FR-010).
    /// </summary>
    Task<PageTranslation> TranslateAsync(
        Document document,
        Page page,
        LanguageCode target,
        LanguageCode? sourceOverride,
        ModelProfile model,
        TranslationOrigin origin,
        CancellationToken ct);
}
