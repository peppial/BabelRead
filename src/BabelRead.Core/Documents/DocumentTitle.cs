using System.Text;
using System.Text.RegularExpressions;

namespace BabelRead.Core.Documents;

/// <summary>
/// Cleans up document titles taken from file metadata. PDFs (and some EPUBs) store "smart" punctuation in
/// the Windows-1252 0x80-0x9F range but expose those bytes as C1 control characters, which render as tofu
/// boxes (e.g. "Centaur[]s" for "Centaur's"). The printable ones are mapped back to Unicode; any remaining
/// control characters are dropped rather than shown as boxes.
/// </summary>
internal static partial class DocumentTitle
{
    private static readonly Dictionary<char, char> Cp1252Punctuation = new()
    {
        ['\u0082'] = '\u201A', // single low-9 quote
        ['\u0084'] = '\u201E', // double low-9 quote
        ['\u0085'] = '\u2026', // horizontal ellipsis
        ['\u0086'] = '\u2020', // dagger
        ['\u0087'] = '\u2021', // double dagger
        ['\u0088'] = '\u02C6', // modifier circumflex
        ['\u008B'] = '\u2039', // single left angle quote
        ['\u0091'] = '\u2018', // left single quote
        ['\u0092'] = '\u2019', // right single quote (curly apostrophe)
        ['\u0093'] = '\u201C', // left double quote
        ['\u0094'] = '\u201D', // right double quote
        ['\u0095'] = '\u2022', // bullet
        ['\u0096'] = '\u2013', // en dash
        ['\u0097'] = '\u2014', // em dash
        ['\u0099'] = '\u2122', // trademark
        ['\u009B'] = '\u203A', // single right angle quote
    };

    public static string Clean(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (Cp1252Punctuation.TryGetValue(ch, out var mapped))
            {
                sb.Append(mapped);
            }
            else if (!char.IsControl(ch) || ch == '\t')
            {
                sb.Append(ch);
            }
            // else: a stray control character (the tofu case) is dropped.
        }

        return WhitespaceRuns().Replace(sb.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRuns();
}
