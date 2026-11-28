using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Sandcastle.Application.Agent;
using Sandcastle.Infrastructure.Scm;

namespace Sandcastle.Infrastructure.Agent.Tools;

/// <summary>SPEC-191: GitHub pull-request tools exposed to the agent loop.</summary>
public static class BuiltinGitHubPrTools
{
    private static JsonElement Schema(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static string? Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int Int(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : fallback;

    private static int RequiredNumber(JsonElement args)
    {
        if (!args.TryGetProperty("number", out var v)
            || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var n) || n <= 0)
        {
            throw new InvalidOperationException("pull_request_number_required");
        }
        return n;
    }

    private static async Task<ToolResult> RunAsync(
        ToolCallContext ctx, Func<ScmAgentPullRequestService, Task<object>> operation)
    {
        var svc = ctx.Services.GetService<ScmAgentPullRequestService>();
        if (svc is null) return ToolResult.Error("github pull request service unavailable");
        try
        {
            return ToolResult.Ok(await operation(svc));
        }
        catch (NotSupportedException)
        {
            return ToolResult.Error("provider_operation_not_supported");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    /// <summary>Read-only PR list scoped to a selected repository.</summary>
    public static AgentTool ListPullRequests() => new(
        new ToolSchema("github_list_pull_requests",
            "List GitHub pull requests for a repository selected by this conversation. Output is bounded and redacted.",
            Schema("""
                {"type":"object","properties":{
                  "repository":{"type":"string","description":"Repository full name or short name; default primary selected repository"},
                  "state":{"type":"string","enum":["open","closed","all"],"default":"open"},
                  "limit":{"type":"integer","minimum":1,"maximum":100,"default":20}},
                 "additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) => await RunAsync(ctx,
            svc => svc.ListAsync(ctx.ConversationId,
                Str(args, "repository"), Str(args, "state"),
                Int(args, "limit", 20), ct)));

    /// <summary>Read-only detailed PR view.</summary>
    public static AgentTool ViewPullRequest() => new(
        new ToolSchema("github_view_pull_request",
            "View a GitHub pull request: metadata, body excerpt, checks/review summary and changed-file counts.",
            Schema("""
                {"type":"object","properties":{
                  "repository":{"type":"string","description":"Repository full name or short name; default primary selected repository"},
                  "number":{"type":"integer","minimum":1}},
                 "required":["number"],"additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) => await RunAsync(ctx,
            svc => svc.ViewAsync(ctx.ConversationId,
                Str(args, "repository"), RequiredNumber(args), ct)));

    /// <summary>Read-only bounded PR diff.</summary>
    public static AgentTool ReadPullRequestDiff() => new(
        new ToolSchema("github_read_pull_request_diff",
            "Read a bounded GitHub pull-request diff with path filtering, patch truncation and binary-file markers.",
            Schema("""
                {"type":"object","properties":{
                  "repository":{"type":"string","description":"Repository full name or short name; default primary selected repository"},
                  "number":{"type":"integer","minimum":1},
                  "path_filter":{"type":"string","description":"Exact path, substring, '*.' extension or 'dir/**' prefix filter"},
                  "max_files":{"type":"integer","minimum":1,"maximum":100,"default":20},
                  "max_patch_chars":{"type":"integer","minimum":200,"maximum":8000,"default":4000}},
                 "required":["number"],"additionalProperties":false}
                """)),
        Risky: false,
        Handler: async (ctx, args, ct) => await RunAsync(ctx,
            svc => svc.DiffAsync(ctx.ConversationId,
                Str(args, "repository"), RequiredNumber(args),
                Str(args, "path_filter"), Int(args, "max_files", 20),
                Int(args, "max_patch_chars", 4_000), ct)));

    /// <summary>Risky PR issue-comment mutation.</summary>
    public static AgentTool CommentPullRequest() => new(
        new ToolSchema("github_comment_pull_request",
            "Comment on a GitHub pull request. This mutates the remote repository and requires the same risky-tool approval path as other write tools.",
            Schema("""
                {"type":"object","properties":{
                  "repository":{"type":"string","description":"Repository full name or short name; default primary selected repository"},
                  "number":{"type":"integer","minimum":1},
                  "body":{"type":"string","maxLength":4000},
                  "idempotency_key":{"type":"string","description":"Optional client-generated idempotency key recorded in audit metadata"}},
                 "required":["number","body"],"additionalProperties":false}
                """)),
        Risky: true,
        Handler: async (ctx, args, ct) => await RunAsync(ctx,
            svc => svc.CommentAsync(ctx.ConversationId,
                Str(args, "repository"), RequiredNumber(args),
                Str(args, "body") ?? "", Str(args, "idempotency_key"), ct)));
}