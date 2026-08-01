using UglyToad.PdfPig.Content;

namespace BabelRead.Core.Documents;

/// <summary>
/// Reconstructs a PDF page's paragraphs from word geometry. PdfPig's <c>Page.Text</c> flattens a page into
/// one run with no paragraph structure, which would make the whole page a single block of text. Instead we
/// group words into visual lines by their baseline, then start a new paragraph wherever a line is indented
/// past the body margin (a first-line indent) or separated from the previous line by an oversized vertical
/// gap (blank-line spacing) — the two conventions PDFs use to mark paragraphs.
/// </summary>
internal static class PdfParagraphExtractor
{
    public static IReadOnlyList<string> Extract(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0)
        {
            return [];
        }

        var lines = GroupIntoLines(words);
        if (lines.Count == 0)
        {
            return [];
        }

        var bodyLeft = lines.Min(l => l.Left);
        var medianGap = Median(NeighbourGaps(lines));
        var indentThreshold = Math.Max(Median(words.Select(w => w.BoundingBox.Height)) * 0.4, 2);

        var paragraphs = new List<string>();
        var current = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0 && StartsNewParagraph(lines[i - 1], lines[i], bodyLeft, medianGap, indentThreshold) && current.Count > 0)
            {
                paragraphs.Add(string.Join(" ", current));
                current.Clear();
            }

            current.Add(lines[i].Text);
        }

        if (current.Count > 0)
        {
            paragraphs.Add(string.Join(" ", current));
        }

        return paragraphs;
    }

    private static bool StartsNewParagraph(Line previous, Line line, double bodyLeft, double medianGap, double indentThreshold)
    {
        var indented = line.Left - bodyLeft > indentThreshold;
        var bigGap = medianGap > 0 && previous.Baseline - line.Baseline > medianGap * 1.5;
        return indented || bigGap;
    }

    private static List<Line> GroupIntoLines(List<Word> words)
    {
        // Words share a line when their baselines are within a fraction of the text height.
        var tolerance = Math.Max(Median(words.Select(w => w.BoundingBox.Height)) * 0.5, 1);
        var lines = new List<Line>();
        foreach (var word in words)
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l.Baseline - word.BoundingBox.Bottom) <= tolerance);
            if (line is null)
            {
                line = new Line(word.BoundingBox.Bottom);
                lines.Add(line);
            }

            line.Add(word);
        }

        lines.Sort((a, b) => b.Baseline.CompareTo(a.Baseline)); // top of page (higher Y) first
        return lines;
    }

    private static IEnumerable<double> NeighbourGaps(List<Line> lines)
    {
        for (var i = 1; i < lines.Count; i++)
        {
            yield return lines[i - 1].Baseline - lines[i].Baseline;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private sealed class Line(double baseline)
    {
        private readonly List<Word> _words = [];

        public double Baseline { get; } = baseline;

        public double Left => _words.Min(w => w.BoundingBox.Left);

        public string Text => string.Join(" ", _words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));

        public void Add(Word word) => _words.Add(word);
    }
}
