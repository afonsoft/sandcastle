using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Agent;
using Sandcastle.Application.Insights;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Conversations;
using Sandcastle.Infrastructure.Eventing;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Insights;

public sealed class SessionInsightService : ISessionInsightService
{
    private readonly AppDbContext _db;
    private readonly ILogger<SessionInsightService> _logger;

    private readonly IServiceProvider _services;
    private readonly IAgentLlmProvider? _llmProvider;
    private readonly IOptions<InsightsOptions> _options;

    public SessionInsightService(
        AppDbContext db,
        ILogger<SessionInsightService> logger,
        IServiceProvider services,
        IAgentLlmProvider? llmProvider = null,
        IOptions<InsightsOptions>? options = null)
    {
        _db = db;
        _logger = logger;
        _services = services;
        _llmProvider = llmProvider;
        _options = options ?? Microsoft.Extensions.Options.Options
            .Create(new InsightsOptions());
    }

    public async Task<SessionInsightDto> AnalyzeSessionAsync(Guid conversationId, bool force = false, CancellationToken ct = default)
    {
        var existing = await _db.SessionInsights
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId, ct);

        var conv = await _db.Conversations.FindAsync(new object[] { conversationId }, ct);
        if (conv == null)
        {
            throw new KeyNotFoundException($"Conversation '{conversationId}' not found");
        }

        // SPEC-166 guardrail: cached L2 is reused only while the conversation
        // has not changed since the insight was generated.
        if (existing != null && !force && existing.CreatedAt >= conv.UpdatedAt)
        {
            return ToDto(existing);
        }

        _logger.LogInformation("Generating session insight for conversation {ConvId}", conversationId);

        // Amostragem de eventos associados para análise heurística
        var events = await _db.Events
            .Where(e => e.ConversationId == conversationId
                && e.TruncatedAtTicks == null)
            .OrderBy(e => e.TimestampTicks)
            .Take(100)
            .ToListAsync(ct);

        var a = BuildAnalysis(events, conv);

        var insightRecord = existing ?? new SessionInsightRecord
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Outcome = a.Outcome,
            CreatedAt = DateTimeOffset.UtcNow
        };

        insightRecord.OrgId = conv.OrgId;
        insightRecord.IssuesJson = JsonSerializer.Serialize(a.Issues);
        insightRecord.DeadEndsJson = JsonSerializer.Serialize(a.DeadEnds);
        insightRecord.PhaseCostsJson = JsonSerializer.Serialize(a.PhaseCosts);
        insightRecord.LearningsJson = JsonSerializer.Serialize(a.Learnings);
        insightRecord.SuggestedPrompt = a.SuggestedPrompt;
        insightRecord.Model = conv.LlmModel ?? "gpt-4o";
        insightRecord.CreatedAt = DateTimeOffset.UtcNow;

        if (_options.Value.LlmAnalysis)
        {
            try
            {
                if (_llmProvider is null)
                {
                    throw new InsightLlmAnalysisException(
                        "LLM provider unavailable for session insight analysis");
                }

                var l2 = await AnalyzeWithLlmAsync(events, a, conv, ct);
                insightRecord.Category = l2.Category;
                insightRecord.SizeTier = l2.SizeTier;
                insightRecord.TimelineJson = JsonSerializer.Serialize(l2.Timeline);
                insightRecord.ActionItemsJson = JsonSerializer.Serialize(l2.ActionItems);
                insightRecord.LlmSummary = l2.Summary;
                insightRecord.LlmAnalysisFailed = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (force)
                {
                    throw new InsightLlmAnalysisException(
                        "L2 session insight analysis failed", ex);
                }

                _logger.LogWarning(ex,
                    "L2 insight analysis failed for {Id}; keeping L1", conversationId);
                insightRecord.LlmAnalysisFailed = true;
            }
        }

        if (existing == null)
        {
            await _db.SessionInsights.AddAsync(insightRecord, ct);
        }

        await _db.SaveChangesAsync(ct);
        await SuggestLearningsAsync(a.Learnings, conv, conversationId, ct);

        return ToDto(insightRecord);
    }

    private sealed record Analysis(
        List<SessionIssue> Issues, List<string> DeadEnds,
        List<string> Learnings, List<PhaseCost> PhaseCosts,
        string Outcome, string? SuggestedPrompt);

    private static Analysis BuildAnalysis(
        List<EventRecord> events, ConversationRecord conv)
    {
        var issues = new List<SessionIssue>();
        var deadEnds = new List<string>();
        var learnings = new List<string>();

        var flagged = events.Where(ev =>
            ev.Payload.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            ev.Payload.Contains("fail", StringComparison.OrdinalIgnoreCase));
        var hasError = flagged.Any();
        issues.AddRange(flagged.Select(ev =>
            new SessionIssue("command_error", "medium",
                SecretRedactor.Redact(
                    ev.Payload[..Math.Min(120, ev.Payload.Length)]))));

        var outcome = conv.ExecutionStatus switch
        {
            "COMPLETED" or "ready" => hasError ? "partial" : "success",
            "FAILED" or "error" => "failed",
            _ => "unclear"
        };

        if (hasError)
        {
            deadEnds.Add("Tentativa de execução de comando ou compilação gerou erro antes da retentativa");
            learnings.Add("Recomenda-se validar pré-requisitos de ambiente e dependências antes de comandos destrutivos");
        }
        else
        {
            learnings.Add("Sequência de passos executada de forma linear sem regressões");
        }

        var phaseCosts = new List<PhaseCost>
        {
            new("planning", 1500, 2.5),
            new("tool_execution", 4200, 8.0),
            new("verification", 1800, 3.2),
        };

        var suggestedPrompt = hasError
            ? $"Seja mais explícito ao definir os arquivos-alvo e adicione a instrução 'verifique com dotnet build antes de concluir' para evitar erros na tarefa original: {conv.Title}"
            : null;

        return new Analysis(issues, deadEnds, learnings, phaseCosts,
            outcome, suggestedPrompt);
    }

    internal sealed record L2Analysis(
        string Category, string SizeTier, string Summary,
        IReadOnlyList<string> ActionItems,
        IReadOnlyDictionary<string, string> Timeline);

    private async Task<L2Analysis> AnalyzeWithLlmAsync(
        IReadOnlyList<EventRecord> events, Analysis analysis,
        ConversationRecord conv, CancellationToken ct)
    {
        var sample = new StringBuilder();
        foreach (var ev in events.Take(100))
        {
            var payload = SecretRedactor.Redact(
                ev.Payload[..Math.Min(300, ev.Payload.Length)]);
            sample.Append("- ").Append(ev.Kind).Append(": ")
                .AppendLine(payload);
            if (sample.Length > 20000) { break; }
        }

        var prompt = """
            Analyze this agent session and return ONLY JSON with this shape:
            {"category":"bugfix|feature|refactor|investigation|ops|other","size_tier":"XS|S|M|L|XL","summary":"one paragraph","action_items":["imperative follow-up"],"timeline":{"phase":"short note"}}

            Conversation title: __TITLE__
            Status: __STATUS__
            Heuristic outcome: __OUTCOME__
            Issue count: __ISSUES__
            Events:
            __SAMPLE__
            """
            .Replace("__TITLE__", conv.Title ?? "")
            .Replace("__STATUS__", conv.ExecutionStatus ?? "")
            .Replace("__OUTCOME__", analysis.Outcome)
            .Replace("__ISSUES__", analysis.Issues.Count.ToString())
            .Replace("__SAMPLE__", sample.ToString());
        var text = new StringBuilder();
        await foreach (var chunk in _llmProvider!.ChatStreamAsync(
            [new LlmMessage("system",
                "You classify completed software-agent sessions. Return strict JSON only."),
                LlmMessage.User(prompt)],
            [], new LlmOptions(Model: conv.LlmModel,
                Temperature: 0.1, MaxTokens: 1200), ct))
        {
            if (chunk is TextDelta delta)
            {
                text.Append(delta.Text);
            }
        }

        return ParseL2(text.ToString());
    }

    internal static L2Analysis ParseL2(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNl >= 0 && lastFence > firstNl)
            {
                trimmed = trimmed[(firstNl + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InsightLlmAnalysisException("L2 response did not contain JSON");
        }

        using var doc = JsonDocument.Parse(trimmed[start..(end + 1)]);
        var root = doc.RootElement;
        static string RequiredString(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new InsightLlmAnalysisException(
                    $"L2 response missing '{name}'");
            }

            return value.GetString()!.Trim();
        }

        var category = RequiredString(root, "category").ToLowerInvariant();
        var sizeTier = RequiredString(root, "size_tier").ToUpperInvariant();
        if (sizeTier is not ("XS" or "S" or "M" or "L" or "XL"))
        {
            throw new InsightLlmAnalysisException("invalid L2 size_tier");
        }

        var summary = RequiredString(root, "summary");
        var actionItems = root.TryGetProperty("action_items", out var actions)
            && actions.ValueKind == JsonValueKind.Array
                ? actions.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Take(10).ToArray()
                : Array.Empty<string>();
        var timeline = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("timeline", out var tl)
            && tl.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in tl.EnumerateObject())
            {
                timeline[item.Name] = item.Value.ValueKind == JsonValueKind.String
                    ? item.Value.GetString() ?? "" : item.Value.ToString();
            }
        }

        return new L2Analysis(category, sizeTier, summary,
            actionItems, timeline);
    }

    // SPEC-63 RF-001a: learnings da sessão → fila de sugestões
    // (fire-and-forget; nunca falha a análise).
    private async Task SuggestLearningsAsync(
        List<string> learnings, ConversationRecord conv,
        Guid conversationId, CancellationToken ct)
    {
        try
        {
            var knowledge = _services?.GetService(
                typeof(Application.Knowledge.IKnowledgeService))
                as Application.Knowledge.IKnowledgeService;
            if (knowledge is not null)
            {
                foreach (var learning in learnings)
                {
                    await knowledge.SuggestAsync(
                        $"learning: {conv.Title ?? conversationId.ToString()[..8]}",
                        learning, "org", conv.SelectedRepository,
                        Application.Knowledge.SuggestionKind.Entry,
                        "session-insight", [conversationId], null, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "knowledge suggestion hook failed for {Id}", conversationId);
        }
    }

    public async Task<SessionInsightDto?> GetSessionInsightAsync(Guid conversationId, CancellationToken ct = default)
    {
        var record = await _db.SessionInsights
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId, ct);

        return record == null ? null : ToDto(record);
    }

    public async Task<InsightsStatsDto> GetStatsAsync(Guid? orgId = null, int? days = null, CancellationToken ct = default)
    {
        var query = _db.SessionInsights.AsNoTracking();
        if (orgId.HasValue)
        {
            query = query.Where(s => s.OrgId == orgId.Value);
        }

        var all = await query.ToListAsync(ct);
        if (days is > 0)
        {
            // SQLite não traduz comparação de DateTimeOffset — o filtro fica
            // em memória neste relatório agregado (já materializado).
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days.Value);
            all = all.Where(x => x.CreatedAt >= cutoff).ToList();
        }

        int total = all.Count;
        int success = all.Count(s => s.Outcome == "success");
        int partial = all.Count(s => s.Outcome == "partial");
        int failed = all.Count(s => s.Outcome == "failed");
        int unclear = all.Count(s => s.Outcome == "unclear");

        double rate = total > 0 ? (double)(success + partial) / total : 1.0;

        var topIssues = new Dictionary<string, int>();
        foreach (var issuesJson in all
            .Where(r => !string.IsNullOrEmpty(r.IssuesJson))
            .Select(r => r.IssuesJson!))
        {
            try
            {
                var issues = JsonSerializer.Deserialize<List<SessionIssue>>(issuesJson);
                if (issues != null)
                {
                    foreach (var kind in issues.Select(i => i.Kind))
                    {
                        topIssues[kind] = topIssues.GetValueOrDefault(kind) + 1;
                    }
                }
            }
            catch
            {
                // Ignora parse malformado em stats
            }
        }

        var sizeDistribution = all
            .Where(x => !string.IsNullOrWhiteSpace(x.SizeTier))
            .GroupBy(x => x.SizeTier!)
            .ToDictionary(g => g.Key, g => g.Count());
        var categories = all
            .Where(x => !string.IsNullOrWhiteSpace(x.Category))
            .GroupBy(x => x.Category!)
            .ToDictionary(g => g.Key, g => g.Count());
        var l2Count = all.Count(x => !string.IsNullOrWhiteSpace(x.LlmSummary));
        var l2Coverage = total > 0 ? (double)l2Count / total : 0;

        return new InsightsStatsDto(total, success, partial, failed, unclear,
            rate, topIssues, sizeDistribution, categories, l2Coverage);
    }

    private static SessionInsightDto ToDto(SessionInsightRecord r)
    {
        var issues = string.IsNullOrEmpty(r.IssuesJson) ? new List<SessionIssue>() : JsonSerializer.Deserialize<List<SessionIssue>>(r.IssuesJson) ?? new List<SessionIssue>();
        var deadEnds = string.IsNullOrEmpty(r.DeadEndsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(r.DeadEndsJson) ?? new List<string>();
        var phaseCosts = string.IsNullOrEmpty(r.PhaseCostsJson) ? new List<PhaseCost>() : JsonSerializer.Deserialize<List<PhaseCost>>(r.PhaseCostsJson) ?? new List<PhaseCost>();
        var learnings = string.IsNullOrEmpty(r.LearningsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(r.LearningsJson) ?? new List<string>();

        var actionItems = string.IsNullOrEmpty(r.ActionItemsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(r.ActionItemsJson)
                ?? new List<string>();
        var timeline = string.IsNullOrEmpty(r.TimelineJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(r.TimelineJson)
                ?? new Dictionary<string, string>();

        return new SessionInsightDto(
            r.Id,
            r.ConversationId,
            r.OrgId,
            r.Outcome,
            issues,
            deadEnds,
            phaseCosts,
            r.SuggestedPrompt,
            learnings,
            r.Model,
            r.CreatedAt,
            r.Category,
            r.SizeTier,
            actionItems,
            r.LlmSummary,
            timeline,
            r.LlmAnalysisFailed);
    }
}