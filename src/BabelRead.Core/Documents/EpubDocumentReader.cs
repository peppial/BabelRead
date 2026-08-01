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
    private const int MinWordBreakSearchWindow = 350;

    /// <summary>
    /// Longest segment we will hand the model, used to break up a runaway paragraph. It is a constant on
    /// purpose: segments must not depend on the page size, or repaginating would change them and orphan
    /// every translation made from them.
    /// </summary>
    private const int MaxSegmentChars = 1200;

    /// <summary>
    /// Runs of blocks shorter than this are merged into one segment (up to <see cref="MaxSegmentChars"/>).
    /// A segment is one translation call, so a table of contents or index — hundreds of one-line entries —
    /// would otherwise become hundreds of separate (and, on a paid model, costly) calls for a single page.
    /// Normal prose paragraphs already clear this bar, so they are left exactly as they are.
    /// </summary>
    private const int MinSegmentChars = 400;

    private readonly object _gate = new();
    private EpubBook? _open;
    private string? _openId;
    private IReadOnlyList<IReadOnlyList<string>> _chapters = [];
    private IReadOnlyList<IReadOnlyList<string>> _pages = [];
    private int _charsPerVirtualPage = SegmentPaginator.DefaultCharsPerPage;

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
            var chapters = BuildChapterSegments(book);
            var pages = SegmentPaginator.Paginate(chapters, _charsPerVirtualPage);
            lock (_gate)
            {
                _open = book;
                _openId = id;
                _chapters = chapters;
                _pages = pages;
            }

            var language = book.Schema.Package.Metadata.Languages.FirstOrDefault()?.Language ?? string.Empty;
            var cleanedTitle = DocumentTitle.Clean(book.Title);
            var title = cleanedTitle.Length > 0 ? cleanedTitle : Path.GetFileNameWithoutExtension(path);
            return new Document(
                id,
                title,
                path,
                DocumentFormat.Epub,
                pages.Count,
                new LanguageCode(language),
                chapters.SelectMany(c => c).ToArray());
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
            _chapters = [];
            _pages = [];
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
            if (_open is not null)
            {
                _pages = SegmentPaginator.Paginate(_chapters, _charsPerVirtualPage); // regroups segments; never changes them
            }

            return true;
        }
    }

    /// <summary>The book's segments in reading order. Independent of page size — this is what makes
    /// translations survive a repagination.</summary>
    private static IReadOnlyList<IReadOnlyList<string>> BuildChapterSegments(EpubBook book)
    {
        var chapters = new List<IReadOnlyList<string>>(book.ReadingOrder.Count);
        foreach (var chapter in book.ReadingOrder)
        {
            chapters.Add(SplitIntoSegments(HtmlToText(chapter.Content)));
        }

        return chapters;
    }

    private static IReadOnlyList<string> SplitIntoSegments(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var blocks = new List<string>();
        foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            blocks.AddRange(SplitLargeBlock(block));
        }

        return CoalesceShortBlocks(blocks);
    }

    /// <summary>
    /// Merges consecutive short blocks (kept apart by their paragraph breaks) into one segment, so a list
    /// or table of contents is a handful of translation calls rather than one per line. A block that already
    /// meets <see cref="MinSegmentChars"/> is emitted on its own, so ordinary paragraphs are never merged;
    /// merging never pushes a segment past <see cref="MaxSegmentChars"/>.
    /// </summary>
    private static List<string> CoalesceShortBlocks(IReadOnlyList<string> blocks)
    {
        var result = new List<string>();
        var buffer = new StringBuilder();
        foreach (var block in blocks)
        {
            if (buffer.Length == 0)
            {
                buffer.Append(block);
            }
            else if (buffer.Length < MinSegmentChars && buffer.Length + 2 + block.Length <= MaxSegmentChars)
            {
                buffer.Append("\n\n").Append(block); // preserve the paragraph break inside the merged segment
            }
            else
            {
                result.Add(buffer.ToString());
                buffer.Clear();
                buffer.Append(block);
            }
        }

        if (buffer.Length > 0)
        {
            result.Add(buffer.ToString());
        }

        return result;
    }

    private static IEnumerable<string> SplitLargeBlock(string block)
    {
        var remaining = block.Trim();
        while (remaining.Length > MaxSegmentChars)
        {
            var splitAt = remaining.LastIndexOf(' ', MaxSegmentChars);
            if (splitAt < MinWordBreakSearchWindow)
            {
                splitAt = MaxSegmentChars;
            }

            yield return remaining[..splitAt].TrimEnd();
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
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
