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
            var (chapters, links, anchors) = BuildChapterSegmentsWithLinks(book);
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
                chapters.SelectMany(c => c).ToArray(),
                links,
                anchors);
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

    /// <summary>The book's segments in reading order, plus any internal hyperlinks mapped onto those
    /// segments. Segments are independent of page size — this is what makes translations survive a
    /// repagination. Link/anchor extraction never influences the segment text: <see cref="HtmlToText"/>
    /// alone decides it.</summary>
    private static (IReadOnlyList<IReadOnlyList<string>> Chapters, IReadOnlyList<DocumentLink> Links, IReadOnlyDictionary<string, LinkTarget> Anchors)
        BuildChapterSegmentsWithLinks(EpubBook book)
    {
        var chapters = new List<IReadOnlyList<string>>(book.ReadingOrder.Count);
        var chapterRanges = new List<IReadOnlyList<(string Text, int Start, int Length)>>(book.ReadingOrder.Count);
        var chapterExtracts = new List<ExtractedChapter?>(book.ReadingOrder.Count);
        var chapterFilePaths = new List<string>(book.ReadingOrder.Count);
        var chapterStartIndex = new List<int>(book.ReadingOrder.Count);
        var running = 0;

        foreach (var chapter in book.ReadingOrder)
        {
            var text = HtmlToText(chapter.Content);
            var ranges = SplitIntoSegmentsWithRanges(text);
            chapters.Add(ranges.Select(r => r.Text).ToList());
            chapterRanges.Add(ranges);
            chapterFilePaths.Add(chapter.FilePath);
            chapterStartIndex.Add(running);
            running += ranges.Count;

            // Trust the extractor's links/anchors only when its text matches the reader's own output
            // for this chapter; otherwise the chapter's segments stand, just without a link overlay.
            var extracted = EpubLinkExtractor.Extract(chapter.Content);
            chapterExtracts.Add(extracted.Text == text ? extracted : null);
        }

        var anchors = BuildAnchorTable(chapterRanges, chapterExtracts, chapterFilePaths, chapterStartIndex);
        var links = BuildLinks(chapterRanges, chapterExtracts, chapterFilePaths, chapterStartIndex, anchors);
        return (chapters, links, anchors);
    }

    /// <summary>Every anchor a link could target: one entry per <c>id</c>/<c>name</c> in the chapter, plus
    /// a bare <c>{file}</c> key (no fragment) for whole-file links, pointing at the chapter's first
    /// segment.</summary>
    private static Dictionary<string, LinkTarget> BuildAnchorTable(
        IReadOnlyList<IReadOnlyList<(string Text, int Start, int Length)>> chapterRanges,
        IReadOnlyList<ExtractedChapter?> chapterExtracts,
        IReadOnlyList<string> chapterFilePaths,
        IReadOnlyList<int> chapterStartIndex)
    {
        var anchors = new Dictionary<string, LinkTarget>();
        for (var c = 0; c < chapterFilePaths.Count; c++)
        {
            var ranges = chapterRanges[c];
            var key = NormalizePath(chapterFilePaths[c]);
            if (ranges.Count > 0)
            {
                anchors[key] = new LinkTarget(chapterStartIndex[c], 0);
            }

            var extracted = chapterExtracts[c];
            if (extracted is null)
            {
                continue;
            }

            foreach (var anchor in extracted.Value.Anchors)
            {
                var (localIndex, offset) = MapOffsetToSegment(ranges, anchor.Offset);
                anchors[$"{key}#{NormalizeFragment(anchor.Id)}"] = new LinkTarget(chapterStartIndex[c] + localIndex, offset);
            }
        }

        return anchors;
    }

    /// <summary>Every link whose href resolves to a key present in <paramref name="anchors"/>. Broken
    /// internal references and external hrefs are dropped — they read as plain text.</summary>
    private static List<DocumentLink> BuildLinks(
        IReadOnlyList<IReadOnlyList<(string Text, int Start, int Length)>> chapterRanges,
        IReadOnlyList<ExtractedChapter?> chapterExtracts,
        IReadOnlyList<string> chapterFilePaths,
        IReadOnlyList<int> chapterStartIndex,
        IReadOnlyDictionary<string, LinkTarget> anchors)
    {
        var links = new List<DocumentLink>();
        for (var c = 0; c < chapterFilePaths.Count; c++)
        {
            var extracted = chapterExtracts[c];
            if (extracted is null)
            {
                continue;
            }

            var ranges = chapterRanges[c];
            foreach (var linkSpan in extracted.Value.Links)
            {
                var targetKey = ResolveHref(linkSpan.Href, chapterFilePaths[c]);
                if (targetKey is null || !anchors.ContainsKey(targetKey))
                {
                    continue;
                }

                var (localIndex, offset) = MapOffsetToSegment(ranges, linkSpan.Start);
                links.Add(new DocumentLink(chapterStartIndex[c] + localIndex, offset, linkSpan.Length, targetKey));
            }
        }

        return links;
    }

    /// <summary>Maps a chapter-text offset to the segment that contains it. Offsets that land in a gap
    /// (whitespace trimmed away between blocks) clamp to whichever segment edge is nearer.</summary>
    private static (int Index, int Offset) MapOffsetToSegment(
        IReadOnlyList<(string Text, int Start, int Length)> ranges, int offset)
    {
        if (ranges.Count == 0)
        {
            return (0, 0);
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            var (_, start, length) = ranges[i];
            if (offset >= start && offset < start + length)
            {
                return (i, offset - start);
            }

            if (offset < start)
            {
                if (i == 0)
                {
                    return (0, 0);
                }

                var (_, prevStart, prevLength) = ranges[i - 1];
                var distanceToPreviousEnd = offset - (prevStart + prevLength);
                var distanceToThisStart = start - offset;
                return distanceToPreviousEnd <= distanceToThisStart ? (i - 1, prevLength) : (i, 0);
            }
        }

        return (ranges.Count - 1, ranges[^1].Length);
    }

    /// <summary>Resolves a raw <c>href</c> against the chapter that contains it, into an anchor-table key.
    /// Returns <see langword="null"/> for external links (<c>http(s)/mailto/tel/data</c> or protocol-relative
    /// <c>//</c>) or an empty href — both are outside this document.</summary>
    private static string? ResolveHref(string href, string chapterFilePath)
    {
        if (string.IsNullOrEmpty(href) || IsExternalHref(href))
        {
            return null;
        }

        var hashIndex = href.IndexOf('#');
        var rawPath = hashIndex < 0 ? href : href[..hashIndex];
        var rawFragment = hashIndex < 0 ? string.Empty : href[(hashIndex + 1)..];
        var path = Uri.UnescapeDataString(rawPath);
        var fragment = NormalizeFragment(rawFragment);

        var targetPath = path.Length == 0
            ? NormalizePath(chapterFilePath)
            : NormalizePath(CombineRelativePath(GetDirectory(chapterFilePath), path));

        return fragment.Length > 0 ? $"{targetPath}#{fragment}" : targetPath;
    }

    private static readonly string[] ExternalSchemes = ["http", "https", "mailto", "tel", "data"];

    private static bool IsExternalHref(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        var colon = href.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var scheme = href[..colon];
        return ExternalSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetDirectory(string filePath)
    {
        var slash = filePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : filePath[..slash];
    }

    private static string CombineRelativePath(string directory, string relativePath) =>
        directory.Length == 0 || relativePath.StartsWith('/') ? relativePath : $"{directory}/{relativePath}";

    /// <summary>Collapses <c>.</c>/<c>..</c> segments and normalizes slash direction; case is left as
    /// stored, matching how EPUB archive paths are compared elsewhere.</summary>
    internal static string NormalizePath(string path)
    {
        var stack = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(segment);
        }

        return string.Join('/', stack);
    }

    private static string NormalizeFragment(string fragment) => Uri.UnescapeDataString(fragment);

    /// <summary>Splits chapter text into segments and reports each emitted segment's <c>[Start,
    /// Start+Length)</c> range within <paramref name="text"/>, so link/anchor offsets recovered from the
    /// chapter's raw text can be mapped onto the segment they end up in.</summary>
    private static IReadOnlyList<(string Text, int Start, int Length)> SplitIntoSegmentsWithRanges(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var blocks = new List<(string Text, int Start, int Length)>();
        var pos = 0;
        while (pos <= text.Length)
        {
            var separatorIndex = text.IndexOf("\n\n", pos, StringComparison.Ordinal);
            var rawEnd = separatorIndex < 0 ? text.Length : separatorIndex;
            var (trimmedStart, trimmedLength) = TrimRange(text, pos, rawEnd - pos);
            if (trimmedLength > 0)
            {
                blocks.AddRange(SplitLargeBlockWithRanges(text, trimmedStart, trimmedLength));
            }

            if (separatorIndex < 0)
            {
                break;
            }

            pos = separatorIndex + 2;
        }

        return CoalesceShortBlocksWithRanges(blocks);
    }

    /// <summary>
    /// Merges consecutive short blocks (kept apart by their paragraph breaks) into one segment, so a list
    /// or table of contents is a handful of translation calls rather than one per line. A block that already
    /// meets <see cref="MinSegmentChars"/> is emitted on its own, so ordinary paragraphs are never merged;
    /// merging never pushes a segment past <see cref="MaxSegmentChars"/>.
    /// </summary>
    private static List<(string Text, int Start, int Length)> CoalesceShortBlocksWithRanges(
        IReadOnlyList<(string Text, int Start, int Length)> blocks)
    {
        var result = new List<(string Text, int Start, int Length)>();
        var buffer = new StringBuilder();
        var bufferStart = 0;
        var bufferEnd = 0;
        foreach (var block in blocks)
        {
            if (buffer.Length == 0)
            {
                buffer.Append(block.Text);
                bufferStart = block.Start;
                bufferEnd = block.Start + block.Length;
            }
            else if (buffer.Length < MinSegmentChars && buffer.Length + 2 + block.Text.Length <= MaxSegmentChars)
            {
                buffer.Append("\n\n").Append(block.Text); // preserve the paragraph break inside the merged segment
                bufferEnd = block.Start + block.Length;
            }
            else
            {
                result.Add((buffer.ToString(), bufferStart, bufferEnd - bufferStart));
                buffer.Clear();
                buffer.Append(block.Text);
                bufferStart = block.Start;
                bufferEnd = block.Start + block.Length;
            }
        }

        if (buffer.Length > 0)
        {
            result.Add((buffer.ToString(), bufferStart, bufferEnd - bufferStart));
        }

        return result;
    }

    private static IEnumerable<(string Text, int Start, int Length)> SplitLargeBlockWithRanges(string text, int start, int length)
    {
        var curStart = start;
        var curLength = length;
        while (curLength > MaxSegmentChars)
        {
            var window = text.Substring(curStart, curLength);
            var splitAt = window.LastIndexOf(' ', MaxSegmentChars);
            if (splitAt < MinWordBreakSearchWindow)
            {
                splitAt = MaxSegmentChars;
            }

            var (headStart, headLength) = TrimEndOnly(text, curStart, splitAt);
            if (headLength > 0)
            {
                yield return (text.Substring(headStart, headLength), headStart, headLength);
            }

            var (tailStart, tailLength) = TrimStartOnly(text, curStart + splitAt, curLength - splitAt);
            curStart = tailStart;
            curLength = tailLength;
        }

        if (curLength > 0)
        {
            yield return (text.Substring(curStart, curLength), curStart, curLength);
        }
    }

    private static (int Start, int Length) TrimRange(string text, int start, int length)
    {
        var (afterStart, afterStartLength) = TrimStartOnly(text, start, length);
        return TrimEndOnly(text, afterStart, afterStartLength);
    }

    private static (int Start, int Length) TrimStartOnly(string text, int start, int length)
    {
        var end = start + length;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return (start, end - start);
    }

    private static (int Start, int Length) TrimEndOnly(string text, int start, int length)
    {
        var end = start + length;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (start, end - start);
    }

    internal static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        return NormalizeHtml(html);
    }

    /// <summary>Shared normalization pipeline used by <see cref="HtmlToText"/> and, with sentinel scalars
    /// spliced into the input, by <see cref="EpubLinkExtractor"/> to recover link/anchor positions.</summary>
    internal static string NormalizeHtml(string marked)
    {
        var noScripts = ScriptStyleRegex().Replace(marked, " ");
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
