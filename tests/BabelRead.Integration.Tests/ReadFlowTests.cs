using BabelRead.Core.Documents;
using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Integration.Tests;

public sealed class ReadFlowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-int").FullName;
    private static readonly ModelProfile Model = new("p1", "Local", ModelKind.Local, "test-model");

    [Fact]
    public async Task Read_translate_prefetch_then_next_turn_is_served_from_cache()
    {
        // Full Core pipeline with a deterministic fake model (slow enough to prove prefetch pre-warms).
        var fake = new FakeChatClient(delay: TimeSpan.FromMilliseconds(50));
        var registry = new DocumentReaderRegistry(new IDocumentReader[] { new PdfDocumentReader() });
        var translation = new TranslationService(new StubChatClientFactory(fake));
        var cache = new TranslationCache();
        var prefetch = new PrefetchCoordinator(translation, cache);

        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "book.pdf"), "Bonjour", "Bonsoir", "Salut");
        var reader = registry.ResolveFor(path);
        var doc = await reader.OpenAsync(path, CancellationToken.None);
        var target = new LanguageCode("en");

        // 1. On-demand translate page 0.
        var page0 = await reader.GetPageAsync(doc, 0, CancellationToken.None);
        var t0 = await translation.TranslateAsync(doc, page0, target, null, Model, TranslationOrigin.OnDemand, CancellationToken.None);
        cache.Set(new TranslationKey(doc.Id, 0, target, Model.ModelId), t0);
        Assert.Equal(TranslationStatus.Completed, t0.Status);

        // 2. Prefetch the next page while the reader reads page 0.
        var context = new PrefetchContext(doc, target, null, Model, (i, ct) => reader.GetPageAsync(doc, i, ct)!);
        prefetch.OnPageSettled(context, currentIndex: 0, ReadingDirection.Forward);
        await prefetch.PendingTask;

        var callsAfterPrefetch = fake.CallCount; // page0 on-demand + page1 prefetch = 2

        // 3. Turn to page 1 → it must already be cached (instant), no new model call.
        Assert.True(cache.TryGet(new TranslationKey(doc.Id, 1, target, Model.ModelId), out var t1));
        Assert.Equal(TranslationOrigin.Prefetch, t1.Origin);
        Assert.Contains("Bonsoir", t1.Text, StringComparison.Ordinal);
        Assert.Equal(2, callsAfterPrefetch);
        Assert.Equal(2, fake.CallCount); // reading page 1 added no model call
    }

    [Fact]
    public async Task Model_failure_surfaces_as_a_failed_translation_not_an_exception()
    {
        var fake = new FakeChatClient(throwOnCall: new HttpRequestException("network down"));
        var translation = new TranslationService(new StubChatClientFactory(fake));
        var path = SampleDocuments.CreatePdf(Path.Combine(_dir, "b.pdf"), "Bonjour");
        var reader = new PdfDocumentReader();
        var doc = await reader.OpenAsync(path, CancellationToken.None);
        var page = await reader.GetPageAsync(doc, 0, CancellationToken.None);

        var result = await translation.TranslateAsync(doc, page, new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
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
