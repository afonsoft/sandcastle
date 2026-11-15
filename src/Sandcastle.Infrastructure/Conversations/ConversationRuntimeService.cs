using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Conversations;
using Sandcastle.Application.Sandbox;
using Sandcastle.Domain.Sandbox;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Conversations;

/// <summary>Resposta de <c>GET /app-conversations/{id}/runtime</c>.</summary>
public sealed record ConversationRuntimeDto(
    IReadOnlyList<RuntimeJobDto> Jobs,
    IReadOnlyList<RuntimeMcpDto> McpServers,
    RuntimeInfoDto Info);

public sealed record RuntimeJobDto(
    string JobId, string Status, string ExitCode, string Name, string Pid);

public sealed record RuntimeMcpDto(
    string Name, string Status, int ToolsCount, string? Error);

public sealed record RuntimeInfoDto(
    string? Model, string? AgentType, string? Repository,
    Guid? SandboxId, string? SandboxStatus, string? SandboxImage,
    bool Rehydrated);

/// <summary>
/// SPEC-155: agrega o estado operacional da run — jobs em background na
/// sandbox (mesmo layout de <c>~/.local/state/sc/jobs</c> das tools
/// <c>job_*</c>), servidores MCP (estado mais recente emitido no ledger
/// <c>conversation_state/mcp_servers</c>) e metadados da conversa.
/// </summary>
public sealed class ConversationRuntimeService(
    AppDbContext db, ISandboxService sandboxes, ISandboxExecutor executor)
{
    private const string JobsDir =
        "/home/openhands/.local/state/sc/jobs";

    public async Task<ConversationRuntimeDto?> GetAsync(
        Guid conversationId, CancellationToken ct)
    {
        var record = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (record is null)
        {
            return null;
        }

        Domain.Sandbox.Sandbox? sandbox = null;
        if (record.SandboxId is { } sid)
        {
            sandbox = await sandboxes.GetAsync(sid, ct);
        }

        return new ConversationRuntimeDto(
            await ListJobsAsync(sandbox, ct),
            await ListMcpAsync(conversationId, ct),
            await BuildInfoAsync(record, sandbox, ct));
    }

    /// <summary>Jobs vivos — mesmo esquema do <c>job_list</c> builtin.</summary>
    private async Task<IReadOnlyList<RuntimeJobDto>> ListJobsAsync(
        Domain.Sandbox.Sandbox? sandbox, CancellationToken ct)
    {
        if (sandbox?.Status is not SandboxStatus.Running)
        {
            return [];
        }

        var cmd = $"for p in {JobsDir}/*.pid; do "
            + "[ -e \"$p\" ] || continue; "
            + "id=$(basename \"$p\" .pid); pid=$(cat \"$p\"); "
            + "if kill -0 \"$pid\" 2>/dev/null; then st=running; rc=-; "
            + $"elif [ -f \"{JobsDir}/$id.exit\" ]; then st=done; "
            + $"rc=$(cat {JobsDir}/$id.exit); "
            + "else st=done; rc=unknown; fi; "
            + $"nm=$(cat {JobsDir}/$id.name 2>/dev/null || echo -); "
            + "echo \"$id|$st|$rc|$nm|$pid\"; done";
        var r = await executor.ExecAsync(
            sandbox, cmd, TimeSpan.FromSeconds(15), ct);
        if (r.ExitCode != 0)
        {
            return [];
        }

        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length >= 5)
            .Select(p => new RuntimeJobDto(p[0], p[1], p[2], p[3], p[4]))
            .ToList();
    }

    /// <summary>Último estado por servidor no ledger
    /// (<c>mcp_servers</c>: connecting → connected, com tools/error).</summary>
    private async Task<IReadOnlyList<RuntimeMcpDto>> ListMcpAsync(
        Guid conversationId, CancellationToken ct)
    {
        var events = await db.Events.AsNoTracking()
            .Where(e => e.ConversationId == conversationId
                && e.Kind == "ConversationStateUpdateEvent"
                && e.TruncatedAtTicks == null)
            .OrderBy(e => e.TimestampTicks)
            .Select(e => e.Payload)
            .ToListAsync(ct);

        var latest = new Dictionary<string, RuntimeMcpDto>(
            StringComparer.Ordinal);
        foreach (var e in events)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e);
            var root = doc.RootElement;
            if (root.TryGetProperty("key", out var k)
                && k.GetString() != "mcp_servers")
            {
                continue;
            }

            if (!root.TryGetProperty("value", out var v))
            {
                continue;
            }

            var name = v.TryGetProperty("server", out var s)
                ? s.GetString() ?? "" : "";
            if (name.Length == 0)
            {
                continue;
            }

            latest[name] = new RuntimeMcpDto(
                name,
                v.TryGetProperty("state", out var st)
                    ? st.GetString() ?? "unknown" : "unknown",
                v.TryGetProperty("tools", out var t)
                    && t.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? t.GetInt32() : 0,
                v.TryGetProperty("error", out var er)
                    && er.ValueKind == System.Text.Json.JsonValueKind.String
                    ? er.GetString() : null);
        }

        return latest.Values.ToList();
    }

    /// <summary>SPEC-154: flag <c>sandbox_rehydrated</c> mais recente.
    /// </summary>
    private async Task<RuntimeInfoDto> BuildInfoAsync(
        ConversationRecord record, Domain.Sandbox.Sandbox? sandbox,
        CancellationToken ct)
    {
        var rehydrated = await db.Events.AsNoTracking()
            .AnyAsync(e => e.ConversationId == record.Id
                && e.Kind == "ConversationStateUpdateEvent"
                && e.Payload.Contains("\"sandbox_rehydrated\""), ct);
        return new RuntimeInfoDto(
            record.LlmModel, record.AgentType, record.SelectedRepository,
            sandbox?.Id, sandbox?.Status.ToString(),
            sandbox?.PoolKey, rehydrated);
    }
}