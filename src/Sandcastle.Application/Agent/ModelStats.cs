namespace Sandcastle.Application.Agent;

/// <summary>
/// SPEC-167 RF-002: adaptive model stats — outcome recording (quality/latency
/// EMA over a ~50-run window) and score lookup used by the route evaluator.
/// Implementations MUST be fail-open: stats never break routing.
/// </summary>
public interface IModelStatsService
{
    /// <summary>Records one outcome for (role, provider, model).</summary>
    Task RecordOutcomeAsync(
        string role, string? provider, string model, bool success,
        long? latencyMs, decimal? costUsd, CancellationToken ct);

    /// <summary>
    /// Quality EMA in (0,1] per <c>provider:model</c> key for the given
    /// candidates. Absent rows map to <see cref="ModelStatsMath.Neutral"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, double>> GetScoresAsync(
        string role, IReadOnlyList<RouteCandidate> candidates,
        CancellationToken ct);

    /// <summary>Snapshot rows for the stats endpoint (null role = all).</summary>
    Task<IReadOnlyList<ModelStatsSnapshot>> ListAsync(
        string? role, CancellationToken ct);
}

/// <summary>SPEC-167 RF-004: read model for the stats endpoint.</summary>
public sealed record ModelStatsSnapshot(
    string Role, string Provider, string Model,
    long Runs, long Successes, long Failures,
    double QualityEma, double? LatencyEmaMs, decimal TotalCostUsd,
    DateTimeOffset LastSeenAt);

/// <summary>
/// SPEC-167 RF-002: pure EMA math — α=2/(N+1) for a ~50-observation
/// window; quality starts at <see cref="InitialQuality"/>.
/// </summary>
public static class ModelStatsMath
{
    /// <summary>EMA window (SPEC-167: 50 runs).</summary>
    public const int Window = 50;

    /// <summary>α for the sliding window.</summary>
    public const double Alpha = 2.0 / (Window + 1);

    /// <summary>EMA seed before the first observation.</summary>
    public const double InitialQuality = 0.5;

    /// <summary>Score for a candidate with no stats row (neutral — explore).</summary>
    public const double Neutral = 1.0;

    /// <summary>Updated quality EMA after one outcome.</summary>
    public static double NextQuality(double ema, bool success) =>
        ema + (Alpha * ((success ? 1.0 : 0.0) - ema));

    /// <summary>Updated latency EMA (first sample wins).</summary>
    public static double NextLatency(double? ema, long latencyMs) =>
        ema is null ? latencyMs
            : ema.Value + (Alpha * (latencyMs - ema.Value));

    /// <summary>Stats lookup key — must match the evaluator's.</summary>
    public static string Key(string? provider, string model) =>
        $"{provider ?? ""}:{model}";

    /// <summary>
    /// Cost factor in (0,1] for a candidate: cheapest priced candidate in
    /// the pool = 1.0; unpriced or single-priced pools = neutral 1.0.
    /// </summary>
    public static double CostFactor(
        RouteCandidate candidate, decimal? minPrice)
    {
        var price = (candidate.PriceInPer1M ?? 0m)
            + (candidate.PriceOutPer1M ?? 0m);
        if (minPrice is not > 0 || price <= 0)
        {
            return Neutral;
        }

        var f = (double)(minPrice.Value / price);
        return f > 1 ? 1.0 : f;
    }
}
