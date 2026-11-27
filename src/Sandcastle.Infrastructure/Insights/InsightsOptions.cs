namespace Sandcastle.Infrastructure.Insights;

/// <summary>SPEC-166 RF-001: session-insights configuration.</summary>
public sealed class InsightsOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Insights";

    /// <summary>Whether L2 LLM analysis is attempted (default true).</summary>
    public bool LlmAnalysis { get; set; } = true;
}

/// <summary>SPEC-166 RF-003: L2 provider/parsing failure surfaced as 502.</summary>
public sealed class InsightLlmAnalysisException(string message, Exception? inner = null)
    : Exception(message, inner);