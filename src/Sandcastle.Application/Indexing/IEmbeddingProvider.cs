namespace Sandcastle.Application.Indexing;

/// <summary>
/// SPEC-165 RF-001: embedding provider abstraction. Implementations are
/// optional — indexing/search degrade to lexical when no model is configured
/// or a call fails.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Whether an embedding model is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Embeds a batch of texts in one provider call.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default);
}