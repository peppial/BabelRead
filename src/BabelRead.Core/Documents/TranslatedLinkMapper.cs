namespace BabelRead.Core.Documents;

/// <summary>
/// Finds where a paragraph's links land in its translation. A link is recorded as an offset into the
/// paragraph's original text, and a translation has neither the same length nor the same word order, so
/// the offsets cannot simply be carried over. What a translation does keep is the paragraph's structure:
/// its line breaks and the separators between navigation labels. Links written as whole lines — tables of
/// contents, section menus, article-title lists — can therefore be paired off with their translations by
/// position. A link buried inside a sentence cannot, and is reported as unmapped rather than guessed at:
/// no link reads better than one underlining the wrong words.
/// </summary>
public static class TranslatedLinkMapper
{
    /// <summary>Maps <paramref name="links"/>, given as ranges within <paramref name="original"/>, onto
    /// ranges within <paramref name="translated"/>. The result is parallel to the input, holding
    /// <see langword="null"/> wherever a link could not be located.</summary>
    public static IReadOnlyList<(int Start, int Length)?> Map(
        string? original, string? translated, IReadOnlyList<(int Start, int Length)> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        var mapped = new (int Start, int Length)?[links.Count];
        if (links.Count == 0 || string.IsNullOrEmpty(original) || string.IsNullOrEmpty(translated))
        {
            return mapped;
        }

        var originalParts = SplitIntoParts(original);
        var translatedParts = SplitIntoParts(translated);
        if (originalParts.Count == 0 || originalParts.Count != translatedParts.Count)
        {
            return mapped; // the translation reshaped the paragraph: nothing can be paired off by position
        }

        // Which parts each link covers whole. A link that only covers part of a part is inside a sentence.
        var coveredParts = new List<int>[links.Count];
        var claimCount = new int[originalParts.Count];
        for (var i = 0; i < links.Count; i++)
        {
            coveredParts[i] = [];
            var (linkStart, linkLength) = links[i];
            for (var p = 0; p < originalParts.Count; p++)
            {
                var (partStart, partLength) = originalParts[p];
                if (linkStart <= partStart && linkStart + linkLength >= partStart + partLength)
                {
                    coveredParts[i].Add(p);
                    claimCount[p]++;
                }
            }
        }

        for (var i = 0; i < links.Count; i++)
        {
            var parts = coveredParts[i];
            if (parts.Count == 0 || parts.Any(p => claimCount[p] > 1))
            {
                continue; // inside a sentence, or contested by another link
            }

            var (firstStart, _) = translatedParts[parts[0]];
            var (lastStart, lastLength) = translatedParts[parts[^1]];
            mapped[i] = (firstStart, lastStart + lastLength - firstStart);
        }

        return mapped;
    }

    /// <summary>Splits text on the structure a translation preserves — line breaks and the separators that
    /// set navigation labels apart — returning each part's trimmed range. Runs of separators count as one,
    /// and empty parts are dropped, so a translation that leaves off a leading or trailing separator still
    /// lines up while one that loses a whole label does not.</summary>
    private static List<(int Start, int Length)> SplitIntoParts(string text)
    {
        var parts = new List<(int Start, int Length)>();
        var pos = 0;
        while (pos < text.Length)
        {
            if (IsSeparator(text[pos]))
            {
                pos++;
                continue;
            }

            var start = pos;
            while (pos < text.Length && !IsSeparator(text[pos]))
            {
                pos++;
            }

            var (trimmedStart, trimmedLength) = Trim(text, start, pos - start);
            if (trimmedLength > 0)
            {
                parts.Add((trimmedStart, trimmedLength));
            }
        }

        return parts;
    }

    private static bool IsSeparator(char c) => c is '\n' or '\r' or '|' or '·' or '•';

    private static (int Start, int Length) Trim(string text, int start, int length)
    {
        var end = start + length;
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (start, end - start);
    }
}
