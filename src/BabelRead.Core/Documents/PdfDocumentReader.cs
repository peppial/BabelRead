using BabelRead.Core.Domain;
using UglyToad.PdfPig;

namespace BabelRead.Core.Documents;

/// <summary>Reads PDF documents via PdfPig. Stateful for the currently-open document (one at a time,
/// v1): keeps the parsed document so pages can be served lazily on navigation.</summary>
public sealed class PdfDocumentReader : IDocumentReader, IDisposable
{
    private readonly object _gate = new();
    private PdfDocument? _open;
    private string? _openId;

    public DocumentFormat Format => DocumentFormat.Pdf;

    public bool CanOpen(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<Document> OpenAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(
            () =>
            {
                try
                {
                    var pdf = PdfDocument.Open(path);
                    var id = DocumentIdentity.FromPath(path);
                    lock (_gate)
                    {
                        _open?.Dispose();
                        _open = pdf;
                        _openId = id;
                    }

                    var title = pdf.Information.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(path);
                    // PDFs carry no reliable language tag; leave source language for the model to detect.
                    return new Document(id, title, path, DocumentFormat.Pdf, pdf.NumberOfPages, LanguageCode.Unknown);
                }
                catch (Exception ex)
                {
                    throw new DocumentOpenException($"Could not open PDF '{Path.GetFileName(path)}'. It may be corrupt or password-protected.", ex);
                }
            },
            ct);
    }

    public Task<Page> GetPageAsync(Document document, int index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Task.Run(
            () =>
            {
                lock (_gate)
                {
                    if (_open is null || _openId != document.Id)
                    {
                        throw new DocumentOpenException("The requested PDF is not open.");
                    }

                    if (index < 0 || index >= _open.NumberOfPages)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    var pdfPage = _open.GetPage(index + 1); // PdfPig is 1-based
                    return new Page(index, pdfPage.Text ?? string.Empty);
                }
            },
            ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _open?.Dispose();
            _open = null;
        }
    }
}
