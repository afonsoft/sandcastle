using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Agent.Routing;

/// <summary>
/// SPEC-167 RF-002/RF-004: <see cref="IModelStatsService"/> over
/// <c>model_stats</c>. Upsert per (role,provider,model) — falha aberta:
/// qualquer erro de DB vira log+no-op, nunca quebra rota nem run.
/// </summary>
public sealed class ModelStatsService(
    AppDbContext db, ILogger<ModelStatsService> logger)
    : IModelStatsService
{
    /// <inheritdoc />
    public async Task RecordOutcomeAsync(
        string role, string? provider, string model, bool success,
        long? latencyMs, decimal? costUsd, CancellationToken ct)
    {
        try
        {
            var prov = provider ?? "";
            var row = await db.ModelStats.FirstOrDefaultAsync(
                s => s.Role == role && s.Provider == prov
                     && s.Model == model, ct);
            if (row is null)
            {
                row = new ModelStatsRecord
                {
                    Id = Guid.NewGuid(), Role = role, Provider = prov,
                    Model = model,
                };
                db.ModelStats.Add(row);
            }

            row.Runs += 1;
            if (success)
            {
                row.Successes += 1;
            }
            else
            {
                row.Failures += 1;
            }

            row.QualityEma = ModelStatsMath.NextQuality(
                row.QualityEma, success);
            if (latencyMs is { } ms)
            {
                row.LatencyEmaMs = ModelStatsMath.NextLatency(
                    row.LatencyEmaMs, ms);
            }

            if (costUsd is { } usd)
            {
                row.TotalCostUsd += usd;
            }

            row.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "model_stats record failed ({Role} {Model}) — skipped",
                role, model);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, double>> GetScoresAsync(
        string role, IReadOnlyList<RouteCandidate> candidates,
        CancellationToken ct)
    {
        var scores = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            var provs = candidates
                .Select(c => c.Provider ?? "").Distinct()
                .ToArray();
            var models = candidates.Select(c => c.Model)
                .Distinct().ToArray();
            var rows = await db.ModelStats.AsNoTracking()
                .Where(s => s.Role == role && provs.Contains(s.Provider)
                            && models.Contains(s.Model))
                .ToListAsync(ct);
            foreach (var c in candidates)
            {
                var row = rows.FirstOrDefault(r =>
                    r.Provider == (c.Provider ?? "")
                    && string.Equals(r.Model, c.Model,
                        StringComparison.OrdinalIgnoreCase));
                scores[ModelStatsMath.Key(c.Provider, c.Model)] =
                    row?.QualityEma ?? ModelStatsMath.Neutral;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e,
                "model_stats read failed ({Role}) — neutral scores", role);
        }

        return scores;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelStatsSnapshot>> ListAsync(
        string? role, CancellationToken ct)
    {
        var q = db.ModelStats.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(role))
        {
            q = q.Where(s => s.Role == role);
        }

        var rows = await q.OrderBy(s => s.Role).ThenBy(s => s.Model)
            .ToListAsync(ct);
        return rows.ConvertAll(r => new ModelStatsSnapshot(
            r.Role, r.Provider, r.Model, r.Runs, r.Successes, r.Failures,
            r.QualityEma, r.LatencyEmaMs, r.TotalCostUsd, r.LastSeenAt));
    }
}
