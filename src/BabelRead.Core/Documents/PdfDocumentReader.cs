using BabelRead.Core.Domain;
using UglyToad.PdfPig;

namespace BabelRead.Core.Documents;

/// <summary>
/// Reads PDF documents via PdfPig. A physical PDF page's translated text is usually far taller than the
/// screen, so — like the EPUB reader — paragraphs are regrouped into viewport-sized virtual pages that fit
/// without scrolling. Physical page boundaries are deliberately ignored (they fall in arbitrary places and
/// would leave stub pages); the whole document is paginated as one continuous run so every page fills.
/// </summary>
public sealed class PdfDocumentReader : IDocumentReader, IReflowableDocumentReader, IDisposable
{
    private readonly object _gate = new();
    private string? _openId;

    // Every paragraph in reading order (one continuous section), and the current virtual pages.
    private IReadOnlyList<string> _segments = [];
    private IReadOnlyList<IReadOnlyList<string>> _pages = [];
    private int _charsPerVirtualPage = SegmentPaginator.DefaultCharsPerPage;

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
                    string title;
                    var segments = new List<string>();

                    // Extract everything up front, then release the file: pages are served from memory.
                    using (var pdf = PdfDocument.Open(path))
                    {
                        var cleaned = DocumentTitle.Clean(pdf.Information.Title);
                        title = cleaned.Length > 0 ? cleaned : Path.GetFileNameWithoutExtension(path);

                        for (var i = 1; i <= pdf.NumberOfPages; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            segments.AddRange(PdfParagraphExtractor.Extract(pdf.GetPage(i)));
                        }
                    }

                    var id = DocumentIdentity.FromPath(path);
                    var pages = SegmentPaginator.Paginate([segments], _charsPerVirtualPage);
                    lock (_gate)
                    {
                        _openId = id;
                        _segments = segments;
                        _pages = pages;
                    }

                    // PDFs carry no reliable language tag; leave source language for the model to detect.
                    return new Document(id, title, path, DocumentFormat.Pdf, pages.Count, LanguageCode.Unknown, segments);
                }
                catch (DocumentOpenException)
                {
                    throw;
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
        lock (_gate)
        {
            if (_openId != document.Id)
            {
                throw new DocumentOpenException("The requested PDF is not open.");
            }

            if (index < 0 || index >= _pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return Task.FromResult(new Page(index, _pages[index]));
        }
    }

    public bool UpdateViewport(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return false;
        }

        var suggested = SegmentPaginator.CharsPerPage(viewportWidth, viewportHeight);
        lock (_gate)
        {
            if (suggested == _charsPerVirtualPage)
            {
                return false;
            }

            _charsPerVirtualPage = suggested;
            _pages = SegmentPaginator.Paginate([_segments], _charsPerVirtualPage); // regroups the same paragraphs
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _openId = null;
            _segments = [];
            _pages = [];
        }
    }
}
