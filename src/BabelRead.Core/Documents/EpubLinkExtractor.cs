using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BabelRead.Core.Documents;

public readonly record struct RawLinkSpan(int Start, int Length, string Href);
public readonly record struct RawAnchor(string Id, int Offset);
public readonly record struct ExtractedChapter(
    string Text, IReadOnlyList<RawLinkSpan> Links, IReadOnlyList<RawAnchor> Anchors);

/// <summary>Recovers link spans and anchor positions from EPUB chapter HTML by threading private-use
/// sentinels through the same normalization the reader uses for text. The caller must treat the result
/// as trustworthy only when <see cref="ExtractedChapter.Text"/> equals the reader's own text output.</summary>
public static partial class EpubLinkExtractor
{
    private const char LinkOpen = '\uE000';
    private const char LinkClose = '\uE001';
    private const char AnchorMark = '\uE002';

    public static ExtractedChapter Extract(string? html)
    {
        // A chapter that already uses these private-use scalars (icon fonts do) would be indistinguishable
        // from our own markers. Refuse it: the empty text can never equal the reader's, so the caller drops
        // this chapter's links instead of pairing hrefs with the wrong spans.
        if (string.IsNullOrEmpty(html) || html.AsSpan().IndexOfAny(LinkOpen, LinkClose, AnchorMark) >= 0)
        {
            return new ExtractedChapter(string.Empty, [], []);
        }

        // 1. Locate <a href>, </a>, and id/name anchors; splice sentinels in, remembering hrefs/ids in order.
        var hrefs = new List<string>();
        var ids = new List<string>();
        var marked = InsertSentinels(html, hrefs, ids);

        // 2. Normalize exactly as the reader does (sentinels are PUA scalars: untouched by every step).
        // Being non-whitespace, a sentinel splits any whitespace run it lands in, so each half collapses on
        // its own and leaves behind whitespace HtmlToText removes outright. Both repairs below put that back:
        // one for runs inside the text, one for the edges NormalizeHtml's trailing Trim() would have eaten.
        var normalized = TrimEdgeWhitespaceAroundSentinels(
            CollapseWhitespaceRunsAroundSentinels(EpubDocumentReader.NormalizeHtml(marked)));

        // 3. Strip sentinels, recording their offsets in the clean text; pair by appearance order.
        var text = new StringBuilder(normalized.Length);
        var links = new List<RawLinkSpan>();
        var anchors = new List<RawAnchor>();
        var openStack = new Stack<(int Start, string Href)>(); // unmatched link opens (handles nesting, LIFO)
        var hrefQueue = new Queue<string>(hrefs);              // hrefs in open order
        var idQueue = new Queue<string>(ids);                 // ids in anchor order
        foreach (var ch in normalized)
        {
            switch (ch)
            {
                case LinkOpen:
                    openStack.Push((text.Length, hrefQueue.Count > 0 ? hrefQueue.Dequeue() : string.Empty));
                    break;
                case LinkClose:
                    if (openStack.Count > 0)
                    {
                        var (start, href) = openStack.Pop();
                        if (text.Length > start) // an <a> whose whole text was whitespace has nothing to click
                        {
                            links.Add(new RawLinkSpan(start, text.Length - start, href));
                        }
                    }
                    break;
                case AnchorMark:
                    var id = idQueue.Count > 0 ? idQueue.Dequeue() : string.Empty;
                    if (id.Length > 0)
                    {
                        anchors.Add(new RawAnchor(id, text.Length));
                    }
                    break;
                default:
                    text.Append(ch);
                    break;
            }
        }

        return new ExtractedChapter(text.ToString(), links, anchors);
    }

    /// <summary>Re-collapses every whitespace run a sentinel split in two, to the single space or break
    /// <see cref="EpubDocumentReader.NormalizeHtml"/> would have produced had the sentinel not been in the
    /// way. Close sentinels come out ahead of the collapsed whitespace and open/anchor sentinels after it,
    /// so a link spans exactly its visible text and an anchor points at the first character following it.
    /// </summary>
    private static string CollapseWhitespaceRunsAroundSentinels(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (!IsCollapsible(s[i]) && !IsSentinel(s[i]))
            {
                sb.Append(s[i++]);
                continue;
            }

            var runStart = i;
            var sentinels = new List<char>();
            var newlines = 0;
            var sawWhitespace = false;
            for (; i < s.Length && (IsCollapsible(s[i]) || IsSentinel(s[i])); i++)
            {
                if (IsSentinel(s[i]))
                {
                    sentinels.Add(s[i]);
                }
                else
                {
                    sawWhitespace = true;
                    newlines += s[i] == '\n' ? 1 : 0;
                }
            }

            if (sentinels.Count == 0)
            {
                sb.Append(s, runStart, i - runStart); // untouched by a sentinel: already normalized
                continue;
            }

            AppendSentinels(sb, sentinels, closes: true);
            sb.Append(!sawWhitespace ? string.Empty : newlines switch
            {
                0 => " ",      // InlineWhitespaceRegex
                1 => "\n",     // SpacesAroundNewlineRegex
                _ => "\n\n",   // ExcessNewlinesRegex
            });
            AppendSentinels(sb, sentinels, closes: false);
        }

        return sb.ToString();
    }

    private static void AppendSentinels(StringBuilder sb, List<char> sentinels, bool closes)
    {
        foreach (var sentinel in sentinels)
        {
            if ((sentinel == LinkClose) == closes)
            {
                sb.Append(sentinel);
            }
        }
    }

    /// <summary>Whitespace <see cref="EpubDocumentReader.NormalizeHtml"/> collapses. Deliberately narrower
    /// than <see cref="char.IsWhiteSpace(char)"/>: its regexes leave NBSP (common in EPUB prose) alone, so
    /// treating NBSP as collapsible here would delete characters the reader's own text keeps.</summary>
    private static bool IsCollapsible(char c) => c is ' ' or '\t' or '\f' or '\v' or '\n';

    /// <summary>Removes whitespace that sits between a string edge and a sentinel, mirroring what
    /// <c>string.Trim()</c> would have done had the (non-whitespace) sentinel not been there to block it.
    /// Sentinels themselves are preserved, just moved to sit flush against the edge.</summary>
    private static string TrimEdgeWhitespaceAroundSentinels(string s)
    {
        var start = 0;
        var leadingSentinels = new StringBuilder();
        while (start < s.Length && (char.IsWhiteSpace(s[start]) || IsSentinel(s[start])))
        {
            if (IsSentinel(s[start]))
            {
                leadingSentinels.Append(s[start]);
            }

            start++;
        }

        var end = s.Length - 1;
        var trailingSentinels = new StringBuilder();
        while (end >= start && (char.IsWhiteSpace(s[end]) || IsSentinel(s[end])))
        {
            if (IsSentinel(s[end]))
            {
                trailingSentinels.Insert(0, s[end]);
            }

            end--;
        }

        if (start == 0 && end == s.Length - 1)
        {
            return s; // no edge whitespace/sentinel mix to repair
        }

        var core = end >= start ? s[start..(end + 1)] : string.Empty;
        return leadingSentinels + core + trailingSentinels.ToString();
    }

    private static bool IsSentinel(char c) => c is LinkOpen or LinkClose or AnchorMark;

    /// <summary>Splice sentinels into the raw HTML: <c>LinkClose</c> just before each <c>&lt;/a&gt;</c>;
    /// after every other tag, <c>AnchorMark</c> when it carries id/name (recording the id) and
    /// <c>LinkOpen</c> when it is an &lt;a&gt; with an href (recording the href).</summary>
    private static string InsertSentinels(string html, List<string> hrefs, List<string> ids)
    {
        var sb = new StringBuilder(html.Length + 16);
        var pos = 0;
        foreach (Match m in TagRegex().Matches(html))
        {
            sb.Append(html, pos, m.Index - pos);
            pos = m.Index + m.Length;
            var tag = m.Value;

            if (CloseAnchorRegex().IsMatch(tag))
            {
                sb.Append(LinkClose).Append(tag); // close sentinel sits before </a>, at the link text end
                continue;
            }

            sb.Append(tag); // the opening/other tag itself
            var id = MatchAttr(IdAttrRegex(), tag) ?? (IsAnchorRegex().IsMatch(tag) ? MatchAttr(NameAttrRegex(), tag) : null);
            if (id is { Length: > 0 })
            {
                ids.Add(WebUtility.HtmlDecode(id));
                sb.Append(AnchorMark);
            }

            if (IsAnchorRegex().IsMatch(tag))
            {
                var href = MatchAttr(HrefAttrRegex(), tag);
                if (href is not null)
                {
                    hrefs.Add(WebUtility.HtmlDecode(href));
                    sb.Append(LinkOpen);
                }
            }
        }

        sb.Append(html, pos, html.Length - pos);
        return sb.ToString();
    }

    // Attribute value (double- or single-quoted); group 1 for "..", group 2 for '..'.
    private static string? MatchAttr(Regex attrRegex, string tag)
    {
        var m = attrRegex.Match(tag);
        if (!m.Success)
        {
            return null;
        }

        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("^</a\\s*>$", RegexOptions.IgnoreCase)]
    private static partial Regex CloseAnchorRegex();

    [GeneratedRegex("^<a\\b", RegexOptions.IgnoreCase)]
    private static partial Regex IsAnchorRegex();

    [GeneratedRegex("\\bid\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex IdAttrRegex();

    [GeneratedRegex("\\bname\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex NameAttrRegex();

    [GeneratedRegex("\\bhref\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttrRegex();
}
