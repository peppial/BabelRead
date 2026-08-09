using BabelRead.Core.Translation;
using Xunit;

namespace BabelRead.Core.Tests.Translation;

public class ContentsTitlesTests
{
    [Fact]
    public void Titles_are_packed_one_per_line_into_as_few_batches_as_they_fit()
    {
        var batches = ContentsTitles.Batch(["Leaders", "Britain", "Culture"]);

        var batch = Assert.Single(batches);
        Assert.Equal("Leaders\nBritain\nCulture", batch.Text);
        Assert.Equal(0, batch.FirstIndex);
        Assert.Equal(3, batch.Count);
    }

    [Fact]
    public void A_batch_is_closed_before_it_outgrows_the_segment_cap()
    {
        var titles = Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 20)).ToArray();

        var batches = ContentsTitles.Batch(titles, maxCharsPerBatch: 50);

        Assert.All(batches, b => Assert.True(b.Text.Length <= 50, $"batch of {b.Text.Length} chars"));
        Assert.Equal(titles.Length, batches.Sum(b => b.Count));                    // none dropped
        Assert.Equal(titles, batches.SelectMany(b => b.Text.Split('\n')));         // none reordered
        Assert.Equal([0, 2, 4, 6, 8], batches.Select(b => b.FirstIndex));          // each knows where it starts
    }

    [Fact]
    public void A_title_spanning_lines_is_flattened_so_the_line_count_stays_the_title_count()
    {
        var batch = Assert.Single(ContentsTitles.Batch(["Chapter 7.\nFailure is a push forward", "Limits"]));

        Assert.Equal("Chapter 7. Failure is a push forward\nLimits", batch.Text);
    }

    [Fact]
    public void A_translated_batch_is_read_back_one_title_per_line()
    {
        var titles = ContentsTitles.Unbatch("Лидери\nБритания\nКултура", expectedCount: 3);

        Assert.Equal(["Лидери", "Британия", "Култура"], titles);
    }

    [Fact]
    public void Blank_lines_and_padding_around_a_translation_are_ignored()
    {
        Assert.Equal(["Лидери", "Британия"], ContentsTitles.Unbatch("\n  Лидери  \n\nБритания\n", expectedCount: 2));
    }

    [Fact]
    public void A_translation_with_the_wrong_number_of_lines_is_refused()
    {
        // Two titles came back as one line (or three): there is no telling which label belongs to which
        // chapter, so the caller falls back to the original titles rather than mislabel the list.
        Assert.Null(ContentsTitles.Unbatch("Лидери и Британия", expectedCount: 2));
        Assert.Null(ContentsTitles.Unbatch("Лидери\nБритания\nКултура", expectedCount: 2));
        Assert.Null(ContentsTitles.Unbatch("   ", expectedCount: 1));
        Assert.Null(ContentsTitles.Unbatch(null, expectedCount: 1));
    }
}
