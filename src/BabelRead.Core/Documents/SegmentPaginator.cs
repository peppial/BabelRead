namespace BabelRead.Core.Documents;

/// <summary>
/// Groups a document's segments (paragraphs) into virtual pages sized to the reading viewport, so every
/// format shows page-like screens that fit rather than an endless scroll. A page is always whole segments,
/// so a translation is exactly the concatenation of its segments' translations and survives repagination.
/// </summary>
internal static class SegmentPaginator
{
    /// <summary>Characters per page at the baseline viewport, before scaling to the real one.</summary>
    public const int DefaultCharsPerPage = 1800;

    private const double BaselineViewportArea = 1280d * 800d;
    private const int MinCharsPerPage = 700;
    private const int MaxCharsPerPage = 5000;

    /// <summary>How far a page may run past the budget to swallow a straddling paragraph before we give up
    /// and break instead. Bounds how much an over-full page can scroll.</summary>
    private const double OverfillTolerance = 1.35;

    /// <summary>How many source characters fit a page of the given viewport, scaled from the baseline.</summary>
    public static int CharsPerPage(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return DefaultCharsPerPage;
        }

        var areaFactor = (viewportWidth * viewportHeight) / BaselineViewportArea;
        var chars = (int)Math.Round(DefaultCharsPerPage * areaFactor, MidpointRounding.AwayFromZero);
        return Math.Clamp(chars, MinCharsPerPage, MaxCharsPerPage);
    }

    /// <summary>
    /// Groups each section's segments into page-sized runs. Sections (EPUB chapters, PDF physical pages)
    /// never merge, so a virtual page always stays within one section. An empty section still yields one
    /// (empty) page, preserving image-only pages.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Paginate(IReadOnlyList<IReadOnlyList<string>> sections, int charsPerPage)
    {
        ArgumentNullException.ThrowIfNull(sections);

        var pages = new List<IReadOnlyList<string>>();
        foreach (var segments in sections)
        {
            if (segments.Count == 0)
            {
                pages.Add([]);
                continue;
            }

            var current = new List<string>();
            var length = 0;
            foreach (var segment in segments)
            {
                if (current.Count > 0)
                {
                    var withSegment = length + 2 + segment.Length;
                    if (withSegment > charsPerPage)
                    {
                        // Adding this segment overflows the budget. Keep it on this page anyway when that
                        // lands closer to the budget than ending here would — otherwise a page stops
                        // half-empty in front of a big paragraph and leaves the window looking bare. But
                        // never let a page run so far past the budget that it turns into a long scroll.
                        var overshoot = withSegment - charsPerPage;
                        var undershoot = charsPerPage - length;
                        if (undershoot <= overshoot || withSegment > charsPerPage * OverfillTolerance)
                        {
                            pages.Add(current);
                            current = [];
                            length = 0;
                        }
                    }
                }

                current.Add(segment);
                length += (length == 0 ? 0 : 2) + segment.Length;
            }

            if (current.Count > 0)
            {
                pages.Add(current);
            }
        }

        return pages;
    }
}
