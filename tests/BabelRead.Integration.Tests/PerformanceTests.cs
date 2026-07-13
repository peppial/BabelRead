using System.Diagnostics;
using BabelRead.Core.Documents;
using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Integration.Tests;

/// <summary>
/// Guards the performance budgets from the spec's success criteria. A fake model with a realistic
/// per-call delay stands in for a real model so the budgets are exercised deterministically.
/// </summary>
public sealed class PerformanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-perf").FullName;
    private static readonly ModelProfile Model = new("p1", "Local", ModelKind.Local, "test-model");

    [Fact]
    public async Task SC003_revisiting_a_cached_page_is_served_in_under_1_second()
    {
        var service = new TranslationService(new StubChatClientFactory(new FakeChatClient(delay: TimeSpan.FromMilliseconds(200))));
        var cache = new TranslationCache();
        var doc = new Document("d", "Doc", "/tmp/d.pdf", DocumentFormat.Pdf, 1, new LanguageCode("fr"));
        var page = new Page(0, "Bonjour");
        var key = new TranslationKey("d", 0, new LanguageCode("en"), Model.ModelId);

        var first = await service.TranslateAsync(doc, page, new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);
        cache.Set(key, first);

        var sw = Stopwatch.StartNew();
        Assert.True(cache.TryGet(key, out _)); // revisit
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"cache revisit took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task SC008_forward_reading_serves_the_next_page_from_prefetch()
    {
        // Model is slow (300ms); prefetch during reading should make the next turn instant.
        var service = new TranslationService(new StubChatClientFactory(new FakeChatClient(delay: TimeSpan.FromMilliseconds(300))));
        var cache = new TranslationCache();
        var prefetch = new PrefetchCoordinator(service, cache);
        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "book.pdf"), "un", "deux", "trois");
        var reader = new PdfDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);
        var target = new LanguageCode("en");

        var context = new PrefetchContext(doc, target, null, Model, (i, ct) => reader.GetPageAsync(doc, i, ct)!);
        prefetch.OnPageSettled(context, currentIndex: 0, ReadingDirection.Forward);
        await prefetch.PendingTask;

        // Turning to page 1 is instant because it was prefetched.
        var sw = Stopwatch.StartNew();
        var hit = cache.TryGet(new TranslationKey(doc.Id, 1, target, Model.ModelId), out _);
        sw.Stop();

        Assert.True(hit);
        Assert.True(sw.ElapsedMilliseconds < 100, $"prefetched turn took {sw.ElapsedMilliseconds}ms");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
