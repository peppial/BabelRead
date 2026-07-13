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

/// <summary>Cache key for a produced translation. Includes language and model so switching either
/// never serves a stale entry (FR-007 / FR-008).</summary>
public readonly record struct TranslationKey(string DocumentId, int PageIndex, LanguageCode TargetLanguage, string ModelId);

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
