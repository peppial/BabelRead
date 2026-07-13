namespace BabelRead.Core.Documents;

/// <summary>Selects the appropriate <see cref="IDocumentReader"/> for a file (by extension/signature).</summary>
public sealed class DocumentReaderRegistry
{
    private readonly IReadOnlyList<IDocumentReader> _readers;

    public DocumentReaderRegistry(IEnumerable<IDocumentReader> readers) => _readers = readers.ToList();

    public bool IsSupported(string path) => _readers.Any(r => r.CanOpen(path));

    /// <summary>Returns a reader that can open <paramref name="path"/>, or throws <see cref="DocumentOpenException"/>.</summary>
    public IDocumentReader ResolveFor(string path) =>
        _readers.FirstOrDefault(r => r.CanOpen(path))
        ?? throw new DocumentOpenException($"Unsupported file type: '{Path.GetExtension(path)}'. Open a PDF or EPUB.");
}
