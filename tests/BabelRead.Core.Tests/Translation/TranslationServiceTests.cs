using BabelRead.Core.Domain;
using BabelRead.Core.Translation;
using BabelRead.TestSupport;
using Xunit;

namespace BabelRead.Core.Tests.Translation;

public class TranslationServiceTests
{
    private static readonly ModelProfile Model = new("p1", "Local", ModelKind.Local, "test-model");

    private static Document Doc(string sourceLang = "fr") =>
        new("doc-1", "Doc", "/tmp/doc.pdf", DocumentFormat.Pdf, 10, new LanguageCode(sourceLang));

    [Fact]
    public async Task Successful_translation_is_completed_and_matches_the_source_page()
    {
        var fake = new FakeChatClient();
        var service = new TranslationService(new StubChatClientFactory(fake), new InMemoryTranslationStore());

        var result = await service.TranslateAsync(
            Doc(), new Page(7, "Bonjour"), new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Equal(7, result.PageIndex); // FR-010
        Assert.Contains("Bonjour", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Source_equal_target_short_circuits_without_calling_the_model()
    {
        var fake = new FakeChatClient();
        var service = new TranslationService(new StubChatClientFactory(fake), new InMemoryTranslationStore());

        var result = await service.TranslateAsync(
            Doc(sourceLang: "en"), new Page(1, "Hello"), new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Equal("Hello", result.Text);
        Assert.Equal(0, fake.CallCount); // model not called
    }

    [Fact]
    public async Task Empty_page_returns_failed_nothing_to_translate()
    {
        var fake = new FakeChatClient();
        var service = new TranslationService(new StubChatClientFactory(fake), new InMemoryTranslationStore());

        var result = await service.TranslateAsync(
            Doc(), new Page(2, "   "), new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.Equal(2, result.PageIndex);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact]
    public async Task Model_error_returns_failed_with_actionable_reason()
    {
        var fake = new FakeChatClient(throwOnCall: new HttpRequestException("boom"));
        var service = new TranslationService(new StubChatClientFactory(fake), new InMemoryTranslationStore());

        var result = await service.TranslateAsync(
            Doc(), new Page(4, "Bonjour"), new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Fact]
    public async Task Unrequested_operation_cancellation_returns_failed_timeout_instead_of_throwing()
    {
        var fake = new FakeChatClient(throwOnCall: new OperationCanceledException("provider timed out"));
        var service = new TranslationService(new StubChatClientFactory(fake), new InMemoryTranslationStore());

        var result = await service.TranslateAsync(
            Doc(), new Page(5, "Bonjour"), new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.Equal("The translation timed out. Try again.", result.FailureReason);
    }

    [Fact]
    public async Task Missing_model_error_returns_actionable_model_specific_reason()
    {
        var fake = new FakeChatClient(throwOnCall: new InvalidOperationException("HTTP 404 (not_found_error) model 'llama3.1' not found"));
        var service = new TranslationService(new StubChatClientFactory(fake), new InMemoryTranslationStore());

        var result = await service.TranslateAsync(
            Doc(), new Page(6, "Bonjour"), new LanguageCode("en"), null, Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.Equal("Model 'llama3.1' is not available on the configured endpoint. Pick an installed Ollama tag in Settings (for example, llama3.1:8b).", result.FailureReason);
    }

    [Fact]
    public async Task Local_model_not_found_retries_with_latest_tag()
    {
        var service = new TranslationService(new StubChatClientFactory(profile =>
            profile.ModelId == "llama3.1"
                ? new FakeChatClient(throwOnCall: new InvalidOperationException("HTTP 404 (not_found_error) model 'llama3.1' not found"))
                : new FakeChatClient()), new InMemoryTranslationStore());

        var result = await service.TranslateAsync(
            Doc(), new Page(8, "Bonjour"), new LanguageCode("en"), null, new ModelProfile("p2", "Local", ModelKind.Local, "llama3.1"), TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Contains("Bonjour", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_model_not_found_retries_with_common_tag_variants()
    {
        var service = new TranslationService(new StubChatClientFactory(profile =>
            profile.ModelId switch
            {
                "llama3.1" => new FakeChatClient(throwOnCall: new InvalidOperationException("HTTP 404 (not_found_error) model 'llama3.1' not found")),
                "llama3.1:latest" => new FakeChatClient(throwOnCall: new InvalidOperationException("HTTP 404 (not_found_error) model 'llama3.1:latest' not found")),
                "llama3.1:8b" => new FakeChatClient(),
                _ => new FakeChatClient(throwOnCall: new InvalidOperationException("unexpected model id"))
            }), new InMemoryTranslationStore());

        var result = await service.TranslateAsync(
            Doc(), new Page(9, "Bonjour"), new LanguageCode("en"), null, new ModelProfile("p2", "Local", ModelKind.Local, "llama3.1"), TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Contains("Bonjour", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_override_takes_precedence_over_detected_language()
    {
        var fake = new FakeChatClient();
        var service = new TranslationService(new StubChatClientFactory(fake), new InMemoryTranslationStore());

        // Detected source is English, but override says German → not a source==target short-circuit for target en.
        var result = await service.TranslateAsync(
            Doc(sourceLang: "en"), new Page(1, "Hallo"), new LanguageCode("en"), new LanguageCode("de"), Model, TranslationOrigin.OnDemand, CancellationToken.None);

        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Equal(1, fake.CallCount); // model WAS called because override(de) != target(en)
        Assert.Equal("de", result.SourceLanguage.Code);
    }
}
