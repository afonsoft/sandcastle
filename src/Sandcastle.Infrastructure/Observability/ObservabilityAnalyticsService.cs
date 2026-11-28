using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Observability;
using Sandcastle.Infrastructure.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Observability;

/// <summary>
/// SPEC-185: admin analytics, run traces, operational budgets and retention.
/// Analytics derive from the immutable V1 event ledger; no rollup schema is
/// required, so SQLite/PostgreSQL stay migration-neutral.
/// </summary>
public sealed class ObservabilityAnalyticsService(
    AppDbContext db,
    IPlanEnforcementService plans,
    IOptions<ObservabilityOptions> options,
    IOptions<BillingOptions> billing,
    IAuditService? audit = null) : IObservabilityAnalyticsService
{
    private sealed record EventFact(
        string Id, Guid ConversationId, string Kind, DateTimeOffset Timestamp,
        string? ParentId, string? Model, long Tokens, decimal Cost,
        long? LatencyMs, string? Name, string? Error, string? ActionKey);

    private static readonly ConcurrentDictionary<string, DateTimeOffset> Alerted = new();

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.GetString() : null;

    private static long Num(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt64(out var n) ? n : 0;

    private static decimal Dec(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDecimal(out var n) ? n : 0m;

    private static EventFact Fact(Infrastructure.Eventing.EventRecord row)
    {
        string? model = null, name = null, error = null, actionKey = null;
        long tokens = 0, latency = 0;
        decimal cost = 0;
        try
        {
            using var doc = JsonDocument.Parse(row.Payload);
            var root = doc.RootElement;
            model = Str(root, "model") ?? Str(root, "model_name");
            name = Str(root, "tool_name") ?? Str(root, "code") ?? row.Kind;
            error = Str(root, "error") ?? Str(root, "detail");
            actionKey = Str(root, "tool_call_id") ?? Str(root, "action_id");
            tokens = Num(root, "prompt_tokens") + Num(root, "completion_tokens");
            cost = Dec(root, "cost_usd");
            latency = Num(root, "latency_ms");
        }
        catch (JsonException)
        {
            name = row.Kind;
        }
        return new EventFact(row.Id, row.ConversationId, row.Kind,
            new DateTimeOffset(row.TimestampTicks, TimeSpan.Zero), row.ParentId,
            model, tokens, cost, latency == 0 ? null : latency,
            name, error, actionKey);
    }

    private async Task<(List<EventFact> Facts, Dictionary<Guid, Infrastructure.Conversations.ConversationRecord> Conversations)> LoadAsync(
        AnalyticsQuery query, CancellationToken ct)
    {
        var days = Math.Clamp(query.Days, 1, 365);
        var since = DateTimeOffset.UtcNow.AddDays(-days).UtcTicks;
        var conversations = await db.Conversations.AsNoTracking()
            .ToListAsync(ct);
        if (query.OrgId is { } org)
            conversations = conversations.Where(c => c.OrgId == org).ToList();
        if (!string.IsNullOrWhiteSpace(query.UserId))
            conversations = conversations.Where(c => c.CreatedByUserId == query.UserId).ToList();
        if (!string.IsNullOrWhiteSpace(query.Status))
            conversations = conversations.Where(c => c.ExecutionStatus.Equals(
                query.Status, StringComparison.OrdinalIgnoreCase)).ToList();
        var ids = conversations.Select(c => c.Id).ToHashSet();
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.TimestampTicks >= since && ids.Contains(e.ConversationId))
            .OrderBy(e => e.TimestampTicks)
            .ToListAsync(ct);
        var facts = rows.Select(Fact).ToList();
        if (!string.IsNullOrWhiteSpace(query.Model))
        {
            var matching = facts.Where(f => f.Model?.Equals(
                query.Model, StringComparison.OrdinalIgnoreCase) == true)
                .Select(f => f.ConversationId).ToHashSet();
            facts = facts.Where(f => matching.Contains(f.ConversationId)).ToList();
        }
        return (facts, conversations.ToDictionary(c => c.Id));
    }

    /// <inheritdoc />
    public async Task<AnalyticsResponse> GetAnalyticsAsync(
        AnalyticsQuery query, CancellationToken ct)
    {
        var (facts, _) = await LoadAsync(query, ct);
        var usage = facts.Where(f => f.Kind == "TokenEvent").ToList();
        var errors = facts.Where(f => f.Kind.Contains("Error", StringComparison.Ordinal)).ToList();
        var runIds = facts.Select(f => f.ConversationId).Distinct().ToList();
        var series = facts.GroupBy(f => DateOnly.FromDateTime(f.Timestamp.UtcDateTime))
            .Select(g => new AnalyticsPoint(g.Key,
                g.Where(x => x.Kind == "TokenEvent").Sum(x => x.Tokens),
                g.Where(x => x.Kind == "TokenEvent").Sum(x => x.Cost),
                g.Select(x => x.ConversationId).Distinct().Count(),
                g.Count(x => x.Kind.Contains("Error", StringComparison.Ordinal))))
            .OrderBy(x => x.Date).ToList();
        var models = usage.GroupBy(f => f.Model ?? "unknown")
            .Select(g => new AnalyticsModelRow(g.Key, "unknown",
                g.Sum(x => x.Tokens), g.Sum(x => x.Cost),
                g.Select(x => x.ConversationId).Distinct().Count(),
                g.Where(x => x.LatencyMs is not null).Select(x => (double?)x.LatencyMs!.Value).Average(),
                errors.Count(e => e.Model == g.Key)))
            .OrderByDescending(x => x.Tokens).ToList();
        return new AnalyticsResponse(series, models,
            usage.Sum(x => x.Tokens), usage.Sum(x => x.Cost),
            runIds.Count, errors.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalyticsRankingRow>> GetRankingsAsync(
        AnalyticsQuery query, string by, CancellationToken ct)
    {
        var (facts, conversations) = await LoadAsync(query, ct);
        var users = await db.Users.AsNoTracking().ToListAsync(ct);
        var orgs = await db.Organizations.AsNoTracking().ToListAsync(ct);
        string Key(Guid cid)
        {
            var c = conversations.GetValueOrDefault(cid);
            return by switch
            {
                "user" => c?.CreatedByUserId ?? "unknown",
                "org" => c?.OrgId?.ToString() ?? "personal",
                _ => cid.ToString(),
            };
        }
        string Label(string key)
        {
            if (by == "user" && Guid.TryParse(key, out var uid))
                return users.FirstOrDefault(u => u.Id == uid)?.Email ?? key;
            if (by == "org" && Guid.TryParse(key, out var oid))
                return orgs.FirstOrDefault(o => o.Id == oid)?.Name ?? key;
            if (by == "conversation" && Guid.TryParse(key, out var cid))
                return conversations.GetValueOrDefault(cid)?.Title ?? key;
            return key;
        }
        return facts.GroupBy(f => Key(f.ConversationId)).Select(g =>
        {
            var usage = g.Where(x => x.Kind == "TokenEvent").ToList();
            return new AnalyticsRankingRow(g.Key, Label(g.Key),
                usage.Sum(x => x.Tokens), usage.Sum(x => x.Cost),
                g.Select(x => x.ConversationId).Distinct().Count(),
                g.Count(x => x.Kind.Contains("Error", StringComparison.Ordinal)));
        }).OrderByDescending(x => x.CostUsd).ThenByDescending(x => x.Tokens)
          .Take(50).ToList();
    }

    /// <inheritdoc />
    public async Task<string> ExportCsvAsync(AnalyticsQuery query, CancellationToken ct)
    {
        var rows = await GetRankingsAsync(query, "conversation", ct);
        var sb = new StringBuilder("id,label,tokens,cost_usd,runs,errors\n");
        foreach (var row in rows)
        {
            static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
            sb.AppendLine(string.Join(',', Csv(row.Id), Csv(row.Label),
                row.Tokens.ToString(CultureInfo.InvariantCulture),
                row.CostUsd.ToString(CultureInfo.InvariantCulture),
                row.Runs, row.Errors));
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperationalBudgetStatus>> GetBudgetsAsync(CancellationToken ct)
    {
        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        var orgs = await db.Organizations.AsNoTracking().ToListAsync(ct);
        var usage = await db.UsageRecords.AsNoTracking()
            .Where(u => u.Period == period).ToListAsync(ct);
        var result = new List<OperationalBudgetStatus>();
        foreach (var org in orgs)
        {
            var row = usage.FirstOrDefault(u => u.OrgId == org.Id);
            var plan = await plans.GetEffectivePlanAsync(org.Id, ct);
            var limit = plan.Limits.MaxTokensPerMonth;
            var pct = limit is > 0 ? row?.TokensUsed / (double)limit : null;
            var level = pct >= 1 ? "exceeded" : pct >= .8 ? "warning" : "ok";
            result.Add(new OperationalBudgetStatus(org.Id, org.Name, plan.Name,
                row?.TokensUsed ?? 0, limit, row?.CostUsd ?? 0, pct, level,
                billing.Value.EnforcementEnabled, level == "ok" ? null : $"token budget {pct:P0}"));
        }
        var personalCost = usage.Where(u => !orgs.Select(o => o.Id).Contains(u.OrgId))
            .Sum(u => u.CostUsd ?? 0);
        if (!orgs.Any()) result.Add(new OperationalBudgetStatus(null, "personal", "free", 0, null, personalCost, null, "ok", billing.Value.EnforcementEnabled, null));
        return result.OrderByDescending(x => x.UsedPercent ?? 0).ToList();
    }

    /// <inheritdoc />
    public async Task<BudgetReconcileResult> ReconcileBudgetsAsync(
        string actorUserId, bool periodic, CancellationToken ct)
    {
        var rows = await GetBudgetsAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows.Where(r => r.Level != "ok"))
        {
            var key = $"{row.OrgId}:{row.Level}";
            if (periodic && Alerted.TryGetValue(key, out var last)
                && now - last < TimeSpan.FromHours(6))
            {
                continue;
            }
            Alerted[key] = now;
            if (audit is not null)
            {
                await audit.RecordAsync("budget_threshold_alert", actorUserId,
                    row.OrgId, "budget", row.OrgId?.ToString() ?? "personal",
                    metadata: new { row.Level, row.TokensUsed, row.TokenLimit, row.UsedPercent, periodic },
                    cancellationToken: ct);
            }
        }
        return new BudgetReconcileResult(rows.Count,
            rows.Count(r => r.Level == "warning"),
            rows.Count(r => r.Level == "exceeded"), now);
    }

    /// <inheritdoc />
    public async Task<RunTraceResponse?> GetRunTraceAsync(
        Guid conversationId, Guid userId, bool isSuperAdmin, CancellationToken ct)
    {
        var conv = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conv is null) return null;
        var uid = userId.ToString();
        var allowed = isSuperAdmin || conv.CreatedByUserId == uid;
        if (!allowed && conv.OrgId is { } org)
        {
            allowed = await db.OrgMembers.AsNoTracking()
                .AnyAsync(m => m.OrgId == org && m.UserId == userId, ct);
        }
        if (!allowed) return null;

        var rows = await db.Events.AsNoTracking()
            .Where(e => e.ConversationId == conversationId)
            .OrderBy(e => e.TimestampTicks).ToListAsync(ct);
        var facts = rows.Select(Fact).ToList();
        var actionStarts = new Dictionary<string, EventFact>(StringComparer.Ordinal);
        foreach (var action in facts.Where(f => f.Kind == "ActionEvent"))
        {
            actionStarts[action.Id] = action;
            if (action.ActionKey is not null) actionStarts[action.ActionKey] = action;
        }
        var items = facts.Where(f => f.Kind is "TokenEvent" or "ActionEvent"
            or "ObservationEvent" or "AgentErrorEvent" or "ConversationErrorEvent"
            or "ServerErrorEvent" or "LLMCompletionLogEvent")
            .Select(f =>
            {
                var type = f.Kind switch
                {
                    "TokenEvent" or "LLMCompletionLogEvent" => "llm",
                    "ActionEvent" or "ObservationEvent" => "tool",
                    _ when f.Kind.Contains("Error", StringComparison.Ordinal) => "error",
                    _ => "event",
                };
                double? duration = null;
                if (f.Kind == "ObservationEvent" && f.ActionKey is not null
                    && actionStarts.TryGetValue(f.ActionKey, out var action))
                {
                    duration = (f.Timestamp - action.Timestamp).TotalMilliseconds;
                }
                return new RunTraceItem(f.Id, f.ParentId, f.Kind, type,
                    f.Name ?? f.Kind, f.Error is null ? "ok" : "error",
                    f.Timestamp, f.Kind == "TokenEvent" ? f.LatencyMs : duration,
                    f.Kind == "TokenEvent" ? f.Tokens : null,
                    f.Kind == "TokenEvent" ? f.Cost : null,
                    f.Model, f.Error);
            }).ToList();
        var usage = facts.Where(f => f.Kind == "TokenEvent").ToList();
        var errors = facts.Count(f => f.Kind.Contains("Error", StringComparison.Ordinal));
        return new RunTraceResponse(conversationId, conv.ExecutionStatus,
            facts.FirstOrDefault()?.Timestamp ?? conv.CreatedAt,
            facts.LastOrDefault()?.Timestamp,
            usage.Sum(f => f.Tokens), usage.Sum(f => f.Cost),
            usage.Count, facts.Count(f => f.Kind == "ActionEvent"), errors, items);
    }

    /// <inheritdoc />
    public async Task<int> CleanupRetainedDiagnosticsAsync(CancellationToken ct)
    {
        if (options.Value.RetentionDays <= 0) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays).UtcTicks;
        var deleted = await db.Events.Where(e => e.TimestampTicks < cutoff)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0 && audit is not null)
        {
            await audit.RecordAsync("observability_retention_cleanup", "system", null,
                "events", null, metadata: new { deleted, options.Value.RetentionDays },
                cancellationToken: ct);
        }
        return deleted;
    }
}