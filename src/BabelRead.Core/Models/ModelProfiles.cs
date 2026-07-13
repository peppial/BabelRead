using BabelRead.Core.Domain;

namespace BabelRead.Core.Models;

/// <summary>Built-in default model profiles. The default is a local, no-key Ollama endpoint so the
/// app works offline out of the box (FR-014); the reader can add/switch profiles in Settings (US2).</summary>
public static class ModelProfiles
{
    public const string DefaultLocalProfileId = "default-local";

    /// <summary>Local Ollama via its OpenAI-compatible endpoint. Model tag is configurable in Settings.</summary>
    public static ModelProfile DefaultLocal(string modelId = "llama3.1") =>
        new(DefaultLocalProfileId, "Local (Ollama)", ModelKind.Local, modelId, new Uri("http://localhost:11434/v1"));
}
