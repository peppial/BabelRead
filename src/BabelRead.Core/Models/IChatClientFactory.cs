using BabelRead.Core.Domain;
using Microsoft.Extensions.AI;

namespace BabelRead.Core.Models;

/// <summary>Thrown when a model profile cannot be turned into a usable client
/// (unknown kind, missing endpoint/credential).</summary>
public sealed class ModelConfigurationException : Exception
{
    public ModelConfigurationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Builds the active <see cref="IChatClient"/> from a <see cref="ModelProfile"/>. This is the single
/// seam (Microsoft Agent Framework / Microsoft.Extensions.AI) where model providers are swapped —
/// cloud (reader-supplied credentials) and local (OpenAI-compatible endpoint) only (FR-007 / FR-014).
/// </summary>
public interface IChatClientFactory
{
    IChatClient Create(ModelProfile profile);
}
