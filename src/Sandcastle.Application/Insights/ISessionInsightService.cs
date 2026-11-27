namespace Sandcastle.Application.Insights;

public sealed record SessionIssue(
    string Kind,
    string Severity,
    string Summary,
    IReadOnlyList<string>? EventIds = null);

public sealed record PhaseCost(
    string Phase,
    long Tokens,
    double Seconds);

public sealed record SessionInsightDto(
    Guid Id,
    Guid ConversationId,
    Guid? OrgId,
    string Outcome,
    IReadOnlyList<SessionIssue> Issues,
    IReadOnlyList<string> DeadEnds,
    IReadOnlyList<PhaseCost> PhaseCosts,
    string? SuggestedPrompt,
    IReadOnlyList<string> Learnings,
    string? Model,
    DateTimeOffset CreatedAt,
    string? Category = null,
    string? SizeTier = null,
    IReadOnlyList<string>? ActionItems = null,
    string? LlmSummary = null,
    IReadOnlyDictionary<string, string>? Timeline = null,
    bool LlmAnalysisFailed = false);

public sealed record InsightsStatsDto(
    int TotalSessionsAnalyzed,
    int SuccessCount,
    int PartialCount,
    int FailedCount,
    int UnclearCount,
    double SuccessRate,
    IReadOnlyDictionary<string, int> TopIssues,
    IReadOnlyDictionary<string, int>? SizeDistribution = null,
    IReadOnlyDictionary<string, int>? Categories = null,
    double L2CoverageRate = 0);

public interface ISessionInsightService
{
    Task<SessionInsightDto> AnalyzeSessionAsync(Guid conversationId, bool force = false, CancellationToken ct = default);
    Task<SessionInsightDto?> GetSessionInsightAsync(Guid conversationId, CancellationToken ct = default);
    Task<InsightsStatsDto> GetStatsAsync(Guid? orgId = null, int? days = null, CancellationToken ct = default);
}