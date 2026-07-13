namespace BabelRead.Core.Domain;

/// <summary>Supported document formats.</summary>
public enum DocumentFormat
{
    Pdf,
    Epub,
}

/// <summary>An opened PDF or EPUB document (spec entity: Document).</summary>
public sealed class Document
{
    public Document(string id, string title, string sourcePath, DocumentFormat format, int pageCount, LanguageCode detectedSourceLanguage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        Id = id;
        Title = title;
        SourcePath = sourcePath;
        Format = format;
        PageCount = pageCount;
        DetectedSourceLanguage = detectedSourceLanguage;
    }

    /// <summary>Stable identity for this source document (derived from path); part of translation-cache keys.</summary>
    public string Id { get; }

    public string Title { get; }

    public string SourcePath { get; }

    public DocumentFormat Format { get; }

    public int PageCount { get; }

    /// <summary>Auto-detected source language; may be <see cref="LanguageCode.Unknown"/> until known.</summary>
    public LanguageCode DetectedSourceLanguage { get; }
}
