using System.Security.Cryptography;
using System.Text;

namespace BabelRead.Core.Domain;

/// <summary>Where a translation came from (FR-008 / FR-015).</summary>
public enum TranslationOrigin
{
    OnDemand,
    Prefetch,
}

/// <summary>Lifecycle status of a page translation (FR-011).</summary>
public enum TranslationStatus
{
    Pending,
    Completed,
    Failed,
}

/// <summary>
/// Identity of a translated segment. It is content-addressed: the source text's hash, plus every input
/// that changes what the model produces — the source and target languages and the model itself, so
/// switching any of them never serves a stale entry (FR-007 / FR-008). Deliberately carries no page index
/// and no document id, so translated work survives repagination, a renamed file, and the same paragraph
/// appearing in another book.
/// </summary>
public readonly record struct TranslationKey(string TextHash, LanguageCode SourceLanguage, LanguageCode TargetLanguage, string ModelId)
{
    public static TranslationKey For(string segmentText, LanguageCode source, LanguageCode target, string modelId) =>
        new(HashText(segmentText), source, target, modelId);

    /// <summary>Short, stable fingerprint of a segment's source text.</summary>
    public static string HashText(string? text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)))[..16].ToLowerInvariant();
}

/// <summary>AI-generated target-language text for one specific page (spec entity: Translation).</summary>
public sealed class PageTranslation
{
    private PageTranslation(
        int pageIndex,
        LanguageCode targetLanguage,
        LanguageCode sourceLanguage,
        string modelId,
        string text,
        TranslationOrigin origin,
        TranslationStatus status,
        string? failureReason)
    {
        PageIndex = pageIndex;
        TargetLanguage = targetLanguage;
        SourceLanguage = sourceLanguage;
        ModelId = modelId;
        Text = text;
        Origin = origin;
        Status = status;
        FailureReason = failureReason;
    }

    /// <summary>Origin page — enforces the page-matching rule (FR-010).</summary>
    public int PageIndex { get; }

    public LanguageCode TargetLanguage { get; }

    public LanguageCode SourceLanguage { get; }

    public string ModelId { get; }

    public string Text { get; }

    public TranslationOrigin Origin { get; }

    public TranslationStatus Status { get; }

    /// <summary>Actionable message when <see cref="Status"/> is <see cref="TranslationStatus.Failed"/>.</summary>
    public string? FailureReason { get; }

    public static PageTranslation Completed(int pageIndex, LanguageCode target, LanguageCode source, string modelId, string text, TranslationOrigin origin) =>
        new(pageIndex, target, source, modelId, text, origin, TranslationStatus.Completed, null);

    public static PageTranslation Failed(int pageIndex, LanguageCode target, string modelId, string reason, TranslationOrigin origin) =>
        new(pageIndex, target, LanguageCode.Unknown, modelId, string.Empty, origin, TranslationStatus.Failed, reason);
}
