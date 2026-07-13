using System.Text.Json;

namespace BabelRead.Core.Models;

/// <summary>Queries Ollama's local API for installed model tags.</summary>
public sealed class OllamaModelCatalog : IOllamaModelCatalog
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(Uri endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        try
        {
            using var client = new HttpClient { Timeout = Timeout };
            using var response = await client.GetAsync(BuildTagsUri(endpoint), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<TagsResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
            return payload?.Models?
                .Select(m => m.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToArray()
                ?? [];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Uri BuildTagsUri(Uri endpoint) =>
        new($"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}/api/tags");

    private sealed class TagsResponse
    {
        public List<TagsModel>? Models { get; set; }
    }

    private sealed class TagsModel
    {
        public string? Name { get; set; }
    }
}
