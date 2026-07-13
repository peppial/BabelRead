namespace BabelRead.Core.Domain;

/// <summary>A single navigable unit of a document (PDF page or EPUB spine reading-order unit).</summary>
public sealed class Page
{
    public Page(int index, string extractableText)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        ExtractableText = extractableText ?? string.Empty;
    }

    /// <summary>Zero-based position within the document.</summary>
    public int Index { get; }

    /// <summary>Readable text of the page; empty for image-only / illustration pages.</summary>
    public string ExtractableText { get; }

    /// <summary>False → the "nothing to translate" empty state.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(ExtractableText);
}
