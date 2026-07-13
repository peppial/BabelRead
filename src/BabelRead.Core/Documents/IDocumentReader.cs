using BabelRead.Core.Domain;

namespace BabelRead.Core.Documents;

/// <summary>Thrown for corrupt, password-protected, or otherwise unopenable documents (spec edge case).</summary>
public sealed class DocumentOpenException : Exception
{
    public DocumentOpenException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Opens a document of one format and exposes its pages. One implementation per format.</summary>
public interface IDocumentReader
{
    /// <summary>The format this reader handles.</summary>
    DocumentFormat Format { get; }

    /// <summary>True if this reader can open the given file (by extension / signature).</summary>
    bool CanOpen(string path);

    /// <summary>Opens the document. Throws <see cref="DocumentOpenException"/> on failure.</summary>
    Task<Document> OpenAsync(string path, CancellationToken ct);

    /// <summary>Returns the page at <paramref name="index"/>; <see cref="Page.HasText"/> is false for image-only pages.</summary>
    Task<Page> GetPageAsync(Document document, int index, CancellationToken ct);
}

/// <summary>Optional capability for readers that can reflow virtual pages to a viewport size.</summary>
public interface IReflowableDocumentReader
{
    /// <summary>
    /// Updates pagination hints based on viewport size. Returns true when pagination changed and the
    /// document should be reopened/reloaded.
    /// </summary>
    bool UpdateViewport(double viewportWidth, double viewportHeight);
}
