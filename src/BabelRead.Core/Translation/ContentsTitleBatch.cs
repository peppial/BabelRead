namespace BabelRead.Core.Translation;

/// <summary>One batch of contents titles, translated as a single unit.</summary>
public readonly record struct ContentsTitleBatch(string Text, int FirstIndex, int Count);

/// <summary>
/// Packs a table of contents into a handful of translatable blocks. Titles are translated through the
/// ordinary segment path, which calls the model once per segment — a hundred chapter titles would be a
/// hundred calls, far too slow for opening a dropdown. Joining them a line at a time makes it a handful,
/// and since a block's text is what keys it in the store, a contents list is translated once for the life
/// of the book. A model that comes back with a different number of lines is refused outright: the reader
/// sees the original titles rather than a list whose labels have slid onto the wrong chapters.
/// </summary>
public static class ContentsTitles
{
    /// <summary>Matches the reader's own segment cap, so a batch is a request the model is used to.</summary>
    public const int MaxCharsPerBatch = 1200;

    public static IReadOnlyList<ContentsTitleBatch> Batch(
        IReadOnlyList<string> titles, int maxCharsPerBatch = MaxCharsPerBatch)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var batches = new List<ContentsTitleBatch>();
        var lines = new List<string>();
        var firstIndex = 0;
        var length = 0;
        for (var i = 0; i < titles.Count; i++)
        {
            var line = OneLine(titles[i]);
            if (lines.Count > 0 && length + 1 + line.Length > maxCharsPerBatch)
            {
                batches.Add(new ContentsTitleBatch(string.Join('\n', lines), firstIndex, lines.Count));
                lines.Clear();
                firstIndex = i;
                length = 0;
            }

            lines.Add(line);
            length += (lines.Count > 1 ? 1 : 0) + line.Length;
        }

        if (lines.Count > 0)
        {
            batches.Add(new ContentsTitleBatch(string.Join('\n', lines), firstIndex, lines.Count));
        }

        return batches;
    }

    /// <summary>Reads a translated batch back into one title per line, or <see langword="null"/> when the
    /// model did not return the line for line it was given.</summary>
    public static IReadOnlyList<string>? Unbatch(string? translated, int expectedCount)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return null;
        }

        var lines = translated
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        return lines.Length == expectedCount ? lines : null;
    }

    /// <summary>A title as one line, so the line count is what tells batches apart.</summary>
    private static string OneLine(string title) =>
        string.Join(' ', (title ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
