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
}
