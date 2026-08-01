using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace BabelRead.App.Reading;

/// <summary>The rendering parameters a page is measured against — must match what the reading
/// text block actually draws with, or measured pages will not line up with rendered ones.</summary>
public readonly record struct ReadingPageMetrics(
    double ColumnWidth,
    double ViewportHeight,
    double FontSize,
    double LineHeight,
    Typeface Typeface,
    FlowDirection FlowDirection);

/// <summary>Cuts the continuous reading flow into viewport-sized visual pages. A page is the run of
/// characters whose wrapped lines fill the viewport height; a paragraph therefore splits across the
/// page break and resumes at the top of the next page (printed-book style).</summary>
public sealed class ReadingPaginator
{
    /// <summary>Characters, starting at <paramref name="start"/>, that fill one page. Never 0 while
    /// text remains: at least the first line is consumed so pagination always advances.</summary>
    public int MeasurePage(string text, int start, ReadingPageMetrics metrics)
    {
        if (string.IsNullOrEmpty(text) || start < 0 || start >= text.Length)
        {
            return 0;
        }

        var maxLines = Math.Max(1, (int)Math.Floor(metrics.ViewportHeight / Math.Max(1, metrics.LineHeight)));
        var remaining = text[start..];

        var layout = new TextLayout(
            remaining,
            metrics.Typeface,
            metrics.FontSize,
            foreground: Brushes.Black,
            textAlignment: TextAlignment.Left,
            textWrapping: TextWrapping.Wrap,
            textTrimming: TextTrimming.None,
            flowDirection: metrics.FlowDirection,
            maxWidth: metrics.ColumnWidth,
            maxHeight: double.PositiveInfinity,
            maxLines: maxLines,
            lineHeight: metrics.LineHeight);

        var consumed = 0;
        foreach (var line in layout.TextLines)
        {
            consumed += line.Length;
        }

        // Guarantee progress: if measuring produced nothing (e.g. an unbreakable token), take one line.
        if (consumed <= 0)
        {
            consumed = layout.TextLines.Count > 0 ? Math.Max(1, layout.TextLines[0].Length) : 1;
        }

        return Math.Min(consumed, remaining.Length);
    }

    /// <summary>The 0-based visual page index and start offset of the page containing
    /// <paramref name="charOffset"/> (clamped into range).</summary>
    public (int PageIndex, int PageStart) PageContaining(string text, int charOffset, ReadingPageMetrics metrics)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, 0);
        }

        var target = Math.Clamp(charOffset, 0, text.Length - 1);
        var start = 0;
        var index = 0;
        while (true)
        {
            var consumed = MeasurePage(text, start, metrics);
            if (consumed <= 0 || start + consumed > target || start + consumed >= text.Length)
            {
                return (index, start);
            }

            start += consumed;
            index++;
        }
    }

    /// <summary>Total number of visual pages in <paramref name="text"/> at these metrics.</summary>
    public int CountPages(string text, ReadingPageMetrics metrics)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var start = 0;
        var pages = 0;
        while (start < text.Length)
        {
            var consumed = MeasurePage(text, start, metrics);
            if (consumed <= 0)
            {
                break;
            }

            start += consumed;
            pages++;
        }

        return pages;
    }
}
