namespace Sandcastle.Application.Observability;

/// <summary>SPEC-185: analytics query window/filters.</summary>
public sealed record AnalyticsQuery(
    int Days = 30, Guid? OrgId = null, string? UserId = null,
    string? Model = null, string? Status = null);

public sealed record AnalyticsPoint(
    DateOnly Date, long Tokens, decimal CostUsd, int Runs, int Errors);

public sealed record AnalyticsModelRow(
    string Model, string Provider, long Tokens, decimal CostUsd,
    int Runs, double? AverageLatencyMs, int Errors);

public sealed record AnalyticsResponse(
    IReadOnlyList<AnalyticsPoint> Series,
    IReadOnlyList<AnalyticsModelRow> Models,
    long TotalTokens, decimal TotalCostUsd, int TotalRuns, int TotalErrors);

public sealed record AnalyticsRankingRow(
    string Id, string Label, long Tokens, decimal CostUsd,
    int Runs, int Errors);

public sealed record RunTraceItem(
    string EventId, string? ParentId, string Kind, string Type,
    string Name, string Status, DateTimeOffset StartedAt,
    double? DurationMs, long? Tokens, decimal? CostUsd,
    string? Model, string? Error);

public sealed record RunTraceResponse(
    Guid ConversationId, string ConversationStatus, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, long TotalTokens, decimal TotalCostUsd,
    int LlmCalls, int ToolCalls, int Errors,
    IReadOnlyList<RunTraceItem> Items);

public sealed record OperationalBudgetStatus(
    Guid? OrgId, string OrgName, string PlanName, long TokensUsed,
    long? TokenLimit, decimal CostUsd, double? UsedPercent,
    string Level, bool EnforcementEnabled, string? Alert);

public sealed record BudgetReconcileResult(
    int Evaluated, int Warning, int Exceeded, DateTimeOffset ReconciledAt);

public sealed class ObservabilityOptions
{
    /// <summary>Days before conversation diagnostics/events are deleted; 0 disables cleanup.</summary>
    public int RetentionDays { get; set; }

    /// <summary>Budget reconcile interval.</summary>
    public int ReconcileIntervalMinutes { get; set; } = 60;
}

public interface IObservabilityAnalyticsService
{
    Task<AnalyticsResponse> GetAnalyticsAsync(AnalyticsQuery query, CancellationToken ct);
    Task<IReadOnlyList<AnalyticsRankingRow>> GetRankingsAsync(
        AnalyticsQuery query, string by, CancellationToken ct);
    Task<string> ExportCsvAsync(AnalyticsQuery query, CancellationToken ct);
    Task<IReadOnlyList<OperationalBudgetStatus>> GetBudgetsAsync(CancellationToken ct);
    Task<BudgetReconcileResult> ReconcileBudgetsAsync(
        string actorUserId, bool periodic, CancellationToken ct);
    Task<RunTraceResponse?> GetRunTraceAsync(
        Guid conversationId, Guid userId, bool isSuperAdmin, CancellationToken ct);
    Task<int> CleanupRetainedDiagnosticsAsync(CancellationToken ct);
}