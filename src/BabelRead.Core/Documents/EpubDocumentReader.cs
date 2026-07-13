using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BabelRead.Core.Domain;
using VersOne.Epub;

namespace BabelRead.Core.Documents;

/// <summary>
/// Reads EPUB documents via VersOne.Epub. To keep the reading pane page-like, each spine item is
/// converted to plain text and split into bounded chunks, so one app "page" stays close to a
/// screenful (instead of a whole chapter that requires scrolling).
/// </summary>
public sealed partial class EpubDocumentReader : IDocumentReader, IReflowableDocumentReader, IDisposable
{
    private const int DefaultCharsPerVirtualPage = 1800;
    private const int MinWordBreakSearchWindow = 350;
    private const double BaselineViewportArea = 1280d * 800d;
    private const int MinCharsPerVirtualPage = 900;
    private const int MaxCharsPerVirtualPage = 5000;

    private readonly object _gate = new();
    private EpubBook? _open;
    private string? _openId;
    private IReadOnlyList<string> _pages = [];
    private int _charsPerVirtualPage = DefaultCharsPerVirtualPage;

    public DocumentFormat Format => DocumentFormat.Epub;

    public bool CanOpen(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase);

    public async Task<Document> OpenAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var book = await EpubReader.ReadBookAsync(path).ConfigureAwait(false);
            var id = DocumentIdentity.FromPath(path);
            var pages = BuildPages(book, _charsPerVirtualPage);
            lock (_gate)
            {
                _open = book;
                _openId = id;
                _pages = pages;
            }

            var language = book.Schema.Package.Metadata.Languages.FirstOrDefault()?.Language ?? string.Empty;
            var title = string.IsNullOrWhiteSpace(book.Title) ? Path.GetFileNameWithoutExtension(path) : book.Title;
            return new Document(id, title, path, DocumentFormat.Epub, pages.Count, new LanguageCode(language));
        }
        catch (DocumentOpenException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DocumentOpenException($"Could not open EPUB '{Path.GetFileName(path)}'. It may be corrupt or unsupported.", ex);
        }
    }

    public Task<Page> GetPageAsync(Document document, int index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            if (_open is null || _openId != document.Id)
            {
                throw new DocumentOpenException("The requested EPUB is not open.");
            }

            if (index < 0 || index >= _pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return Task.FromResult(new Page(index, _pages[index]));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _open = null;
            _pages = [];
        }
    }

    public bool UpdateViewport(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return false;
        }

        var suggested = CalculateCharsPerPage(viewportWidth, viewportHeight);
        lock (_gate)
        {
            if (suggested == _charsPerVirtualPage)
            {
                return false;
            }

            _charsPerVirtualPage = suggested;
            if (_open is not null)
            {
                _pages = BuildPages(_open, _charsPerVirtualPage);
            }

            return true;
        }
    }

    private static IReadOnlyList<string> BuildPages(EpubBook book, int charsPerPage)
    {
        var pages = new List<string>(book.ReadingOrder.Count);
        foreach (var chapter in book.ReadingOrder)
        {
            var text = HtmlToText(chapter.Content);
            var chunks = ChunkText(text, charsPerPage);
            if (chunks.Count == 0)
            {
                pages.Add(string.Empty);
                continue;
            }

            pages.AddRange(chunks);
        }

        return pages;
    }

    private static IReadOnlyList<string> ChunkText(string text, int charsPerPage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chunks = new List<string>();
        var current = new StringBuilder(charsPerPage + 64);
        var blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var block in blocks)
        {
            foreach (var part in SplitLargeBlock(block, charsPerPage))
            {
                if (current.Length == 0)
                {
                    current.Append(part);
                    continue;
                }

                if (current.Length + 2 + part.Length <= charsPerPage)
                {
                    current.Append("\n\n").Append(part);
                    continue;
                }

                chunks.Add(current.ToString());
                current.Clear();
                current.Append(part);
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    private static IEnumerable<string> SplitLargeBlock(string block, int charsPerPage)
    {
        var remaining = block.Trim();
        while (remaining.Length > charsPerPage)
        {
            var splitAt = remaining.LastIndexOf(' ', charsPerPage);
            if (splitAt < MinWordBreakSearchWindow)
            {
                splitAt = charsPerPage;
            }

            yield return remaining[..splitAt].TrimEnd();
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    private static int CalculateCharsPerPage(double viewportWidth, double viewportHeight)
    {
        var areaFactor = (viewportWidth * viewportHeight) / BaselineViewportArea;
        var chars = (int)Math.Round(DefaultCharsPerVirtualPage * areaFactor, MidpointRounding.AwayFromZero);
        return Math.Clamp(chars, MinCharsPerVirtualPage, MaxCharsPerVirtualPage);
    }

    internal static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var noScripts = ScriptStyleRegex().Replace(html, " ");
        var withLineBreaks = BrTagRegex().Replace(noScripts, "\n");
        var withParagraphs = BlockTagRegex().Replace(withLineBreaks, "\n\n");
        var noTags = TagRegex().Replace(withParagraphs, " ");
        var decoded = WebUtility.HtmlDecode(noTags).Replace("\u00AD", string.Empty, StringComparison.Ordinal); // soft hyphen
        var normalized = decoded.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = SpacesAroundNewlineRegex().Replace(normalized, "\n");
        normalized = InlineWhitespaceRegex().Replace(normalized, " ");
        normalized = ExcessNewlinesRegex().Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    [GeneratedRegex("</?(?:address|article|aside|blockquote|caption|dd|div|dl|dt|figcaption|figure|footer|h[1-6]|header|hr|li|main|nav|ol|p|pre|section|table|tbody|td|tfoot|th|thead|tr|ul)\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex("[ \\t\\f\\v]*\\n[ \\t\\f\\v]*")]
    private static partial Regex SpacesAroundNewlineRegex();

    [GeneratedRegex("[ \\t\\f\\v]+")]
    private static partial Regex InlineWhitespaceRegex();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex ExcessNewlinesRegex();
}
