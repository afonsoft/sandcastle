namespace Sandcastle.Domain.Sandbox;

/// <summary>
/// Métricas estendidas e telemetria granular de execuções de run (SPEC-20261102).
/// </summary>
public sealed record SandboxRunTelemetry(
    Guid RunId,
    long TotalTokensUsed,
    double ExecutionTimeMs,
    int ToolCallsCount,
    string ModelUsed,
    DateTimeOffset RecordedAt);