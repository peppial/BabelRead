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
    public Document(
        string id,
        string title,
        string sourcePath,
        DocumentFormat format,
        int pageCount,
        LanguageCode detectedSourceLanguage,
        IReadOnlyList<string>? segments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        Id = id;
        Title = title;
        SourcePath = sourcePath;
        Format = format;
        PageCount = pageCount;
        DetectedSourceLanguage = detectedSourceLanguage;
        Segments = segments ?? [];
    }

    /// <summary>Stable identity for this source document (derived from path); part of translation-cache keys.</summary>
    public string Id { get; }

    public string Title { get; }

    public string SourcePath { get; }

    public DocumentFormat Format { get; }

    /// <summary>Pages are a display grouping and change with the window and font.</summary>
    public int PageCount { get; }

    /// <summary>Every segment in the book, in reading order. Fixed for the life of the file: the unit
    /// translations are keyed to, and the denominator of translation progress.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>Auto-detected source language; may be <see cref="LanguageCode.Unknown"/> until known.</summary>
    public LanguageCode DetectedSourceLanguage { get; }
}
