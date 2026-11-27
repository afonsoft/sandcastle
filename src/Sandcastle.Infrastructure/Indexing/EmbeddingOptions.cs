namespace Sandcastle.Infrastructure.Indexing;

/// <summary>SPEC-165 RF-001: optional embedding model configuration.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Embeddings";

    /// <summary>OpenAI-compatible embedding model. Empty disables semantic search.</summary>
    public string? Model { get; set; }

    /// <summary>Optional endpoint override; defaults to the agent LLM gateway.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Optional API key override; defaults to the agent LLM key.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>SPEC-165 RF-001/RF-003: hybrid search limits.</summary>
public sealed class SearchOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Search";

    /// <summary>Maximum texts sent in one embeddings call (cost budget).</summary>
    public int MaxEmbeddingsPerCall { get; set; } = 512;

    /// <summary>Reciprocal-rank-fusion constant.</summary>
    public int RrfK { get; set; } = 60;
}