using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Agent;
using Sandcastle.Domain.Events;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Agent.Tools;

/// <summary>SPEC-193: explicit run-cognition tools — goal, bounded wait and memory reflection.</summary>
public static class BuiltinCognitionTools
{
    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static string? Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int Int(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : fallback;

    private static string Cap(string? text, int max)
    {
        var value = SecretRedactor.Redact(text ?? "").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    /// <summary>Records the current objective as durable run activity.</summary>
    public static AgentTool Goal() => new(
        new ToolSchema("goal",
            "Record the current run objective so it is visible in the activity stream. Use when the objective changes or needs to be restated.",
            Schema("""
                {"type":"object","properties":{
                  "objective":{"type":"string","maxLength":500}},
                 "required":["objective"],"additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var objective = Cap(Str(args, "objective"), 500);
            if (objective.Length == 0) return ToolResult.Error("objective_required");
            await ctx.EmitAsync(new RunGoalEvent
            {
                Objective = objective,
                ToolCallId = ctx.ToolCallId,
                ActionId = ctx.ActionId,
                ParentId = ctx.ActionId,
            }, ct);
            return ToolResult.Ok(new { objective, recorded = true });
        });

    /// <summary>Bounded wait, optionally probing one background job.</summary>
    public static AgentTool Wait() => new(
        new ToolSchema("wait",
            "Wait a bounded number of seconds (max 30), optionally checking a background job_id afterward. Use for elapsed time or background work; never for indefinite blocking.",
            Schema("""
                {"type":"object","properties":{
                  "seconds":{"type":"integer","minimum":1,"maximum":30,"default":5},
                  "job_id":{"type":"string","description":"Optional job id from job_run/job_list"}},
                 "additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var seconds = Math.Clamp(Int(args, "seconds", 5), 1, 30);
            var jobId = Str(args, "job_id");
            if (jobId is not null && jobId.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_'))
            {
                return ToolResult.Error("job_id_invalid");
            }
            await ctx.EmitAsync(new ToolProgressEvent
            {
                ToolCallId = ctx.ToolCallId ?? "",
                ActionId = ctx.ActionId,
                Phase = "waiting",
                Message = $"waiting {seconds}s" + (jobId is null ? "" : $" for job {jobId}"),
            }, ct);
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            string? status = null;
            string? output = null;
            if (jobId is not null)
            {
                var dir = "/home/openhands/.local/state/sc/jobs";
                var result = await ctx.Executor.ExecAsync(ctx.Sandbox,
                    $"if [ ! -f {dir}/{jobId}.pid ]; then echo not-found; else "
                    + $"pid=$(cat {dir}/{jobId}.pid); "
                    + $"{{ kill -0 \"$pid\" 2>/dev/null && echo running || echo done; }}; "
                    + $"tail -n 20 {dir}/{jobId}.log 2>/dev/null; fi",
                    TimeSpan.FromSeconds(10), ct);
                var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                status = lines.FirstOrDefault() is "running" or "done" ? lines[0] : "unknown";
                output = Cap(string.Join('\n', lines.Skip(1)), 2_000);
            }
            return ToolResult.Ok(new
            {
                waited_seconds = seconds,
                elapsed = true,
                job_id = jobId,
                job_status = status,
                job_output_tail = output,
            });
        });

    /// <summary>Bounded memory/knowledge digest with provenance.</summary>
    public static AgentTool MemoryReflect() => new(
        new ToolSchema("memory_reflect",
            "Reflect over relevant working/org memory and knowledge entries and return a bounded digest with provenance for citation.",
            Schema("""
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Optional relevance query"},
                  "limit":{"type":"integer","minimum":1,"maximum":10,"default":5}},
                 "additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) =>
        {
            var db = ctx.Services.GetService<AppDbContext>();
            if (db is null) return ToolResult.Error("memory store unavailable");
            var query = Str(args, "query")?.Trim();
            var limit = Math.Clamp(Int(args, "limit", 5), 1, 10);
            var entries = new List<object>();

            var convRows = await db.ConversationMemories.AsNoTracking()
                .Where(m => m.ConversationId == ctx.ConversationId)
                .Take(100).ToListAsync(ct);
            foreach (var row in convRows
                .OrderByDescending(m => m.UpdatedAt)
                .Where(m => Matches(m.Key + "\n" + m.Value, query)).Take(limit))
            {
                entries.Add(new
                {
                    source = "conversation_memory",
                    key = row.Key,
                    excerpt = Cap(row.Value, 240),
                    updated_at = row.UpdatedAt,
                    provenance = $"conversation:{ctx.ConversationId}/memory/{row.Key}",
                });
            }

            var conv = await db.Conversations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == ctx.ConversationId, ct);
            if (conv?.OrgId is { } orgId)
            {
                var orgRows = await db.MemoryRecords.AsNoTracking()
                    .Where(m => m.OrgId == orgId && m.DeletedAt == null)
                    .Take(200).ToListAsync(ct);
                foreach (var row in orgRows
                    .OrderByDescending(m => m.UpdatedAt)
                    .Where(m => Matches(m.Key + "\n" + m.Text, query))
                    .Take(Math.Max(0, limit - entries.Count)))
                {
                    entries.Add(new
                    {
                        source = "org_memory",
                        key = row.Key,
                        excerpt = Cap(row.Text, 240),
                        updated_at = row.UpdatedAt,
                        provenance = $"memory:{row.Id}",
                    });
                }
            }

            if (conv is not null)
            {
                var collectionIds = await db.KnowledgeCollections.AsNoTracking()
                    .Where(c => (conv.OrgId != null && c.OrgId == conv.OrgId)
                        || (conv.CreatedByUserId != null && c.OwnerUserId == conv.CreatedByUserId))
                    .Select(c => c.Id).Take(100).ToListAsync(ct);
                var docRows = await db.KnowledgeDocuments.AsNoTracking()
                    .Where(d => collectionIds.Contains(d.CollectionId))
                    .Take(200).ToListAsync(ct);
                var docIds = docRows.Select(d => d.Id).ToHashSet();
                var chunks = await db.KnowledgeChunks.AsNoTracking()
                    .Where(c => docIds.Contains(c.DocumentId))
                    .Take(500).ToListAsync(ct);
                foreach (var chunk in chunks.Where(c => Matches(c.Text, query))
                    .Take(Math.Max(0, limit - entries.Count)))
                {
                    var doc = docRows.First(d => d.Id == chunk.DocumentId);
                    entries.Add(new
                    {
                        source = "knowledge_chunk",
                        key = doc.Title,
                        excerpt = Cap(chunk.Text, 240),
                        updated_at = chunk.CreatedAt,
                        provenance = $"knowledge:{chunk.Id}",
                    });
                }
            }

            return ToolResult.Ok(new
            {
                query,
                count = entries.Count,
                truncated = entries.Count == limit,
                entries,
            });
        });

    private static bool Matches(string haystack, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}