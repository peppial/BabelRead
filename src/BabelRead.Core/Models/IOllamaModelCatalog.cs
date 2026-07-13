namespace BabelRead.Core.Models;

/// <summary>Lists locally available Ollama model tags from the configured endpoint.</summary>
public interface IOllamaModelCatalog
{
    Task<IReadOnlyList<string>> ListAvailableModelsAsync(Uri endpoint, CancellationToken ct = default);
}
