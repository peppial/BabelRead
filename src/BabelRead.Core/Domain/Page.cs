namespace BabelRead.Core.Domain;

/// <summary>
/// A single navigable unit of a document (PDF page or EPUB virtual page). A page is always a whole
/// number of <see cref="Segments"/> — the stable, layout-independent units the translator works in — so
/// repaginating a book re-groups segments without ever changing them, and translated work is never lost.
/// </summary>
public sealed class Page
{
    public Page(int index, string extractableText)
        : this(index, SplitIntoSegments(extractableText))
    {
    }

    public Page(int index, IReadOnlyList<string> segments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(segments);
        Index = index;
        Segments = segments;
        ExtractableText = string.Join("\n\n", segments);
    }

    /// <summary>Zero-based position within the document.</summary>
    public int Index { get; }

    /// <summary>The paragraphs this page is made of; each is translated and stored on its own.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>Readable text of the page; empty for image-only / illustration pages.</summary>
    public string ExtractableText { get; }

    /// <summary>False → the "nothing to translate" empty state.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(ExtractableText);

    /// <summary>Splits page text into paragraphs on blank lines.</summary>
    public static IReadOnlyList<string> SplitIntoSegments(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
