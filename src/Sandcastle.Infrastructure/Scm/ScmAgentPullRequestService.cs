using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Identity;
using Sandcastle.Application.Scm;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Infrastructure.Scm;

/// <summary>
/// SPEC-191: read/review/comment operations for GitHub PRs scoped to the
/// repositories selected by a conversation. Output is bounded and redacted;
/// mutating calls are audit-logged with caller idempotency metadata.
/// </summary>
public sealed partial class ScmAgentPullRequestService(
    AppDbContext db,
    IScmConnectionService connections,
    IScmProviderRegistry providers,
    IAuditService? audit = null)
{
    /// <summary>List pull requests for a repository selected by the conversation.</summary>
    public async Task<object> ListAsync(
        Guid conversationId, string? repository, string? state,
        int limit, CancellationToken ct)
    {
        var (conv, repo, provider, connection) = await ResolveAsync(
            conversationId, repository, ct);
        var normalized = state?.ToLowerInvariant() is "closed" or "all"
            ? state.ToLowerInvariant() : "open";
        var rows = await provider.ListPullRequestsAsync(
            connection, repo, normalized, Math.Clamp(limit, 1, 100), ct);
        return new
        {
            repository = repo,
            state = normalized,
            count = rows.Count,
            pull_requests = rows.Select(p => new
            {
                url = p.Url,
                p.Number,
                state = p.State,
                title = Sanitize(p.Title),
                author = p.Author,
                draft = p.Draft,
                head_ref = p.HeadRef,
                base_ref = p.BaseRef,
                updated_at = p.UpdatedAt,
            }).ToArray(),
        };
    }

    /// <summary>Return deterministic bounded PR details.</summary>
    public async Task<object> ViewAsync(
        Guid conversationId, string? repository, int number, CancellationToken ct)
    {
        var (_, repo, provider, connection) = await ResolveAsync(
            conversationId, repository, ct);
        var details = await provider.GetPullRequestDetailsAsync(
            connection, repo, number, ct)
            ?? throw new InvalidOperationException("pull_request_not_found");
        return new
        {
            repository = repo,
            url = details.Url,
            number = details.Number,
            state = details.State,
            merged = details.Merged,
            title = Sanitize(details.Title),
            author = details.Author,
            draft = details.Draft,
            head_sha = details.HeadSha,
            head_ref = details.HeadRef,
            base_ref = details.BaseRef,
            body_excerpt = Sanitize(details.BodyExcerpt),
            changed_files = details.ChangedFiles,
            additions = details.Additions,
            deletions = details.Deletions,
            checks = new
            {
                total = details.ChecksTotal,
                passing = details.ChecksPassing,
                failing = details.ChecksFailing,
                pending = details.ChecksPending,
            },
            reviews = new
            {
                approved = details.ReviewsApproved,
                changes_requested = details.ReviewsChangesRequested,
            },
            updated_at = details.UpdatedAt,
        };
    }

    /// <summary>Return a bounded unified-diff projection with binary markers.</summary>
    public async Task<object> DiffAsync(
        Guid conversationId, string? repository, int number,
        string? pathFilter, int maxFiles, int maxPatchChars, CancellationToken ct)
    {
        var (_, repo, provider, connection) = await ResolveAsync(
            conversationId, repository, ct);
        var files = await provider.GetPullRequestFilesAsync(
            connection, repo, number, ct);
        var filtered = files.Where(f => MatchesPath(f, pathFilter)).ToArray();
        var perFileCap = Math.Clamp(maxPatchChars, 200, 8_000);
        var totalCap = 24_000;
        var used = 0;
        var rows = new List<object>();
        foreach (var file in filtered.Take(Math.Clamp(maxFiles, 1, 100)))
        {
            var binary = file.Patch is null;
            var patch = file.Patch ?? "[binary or patch omitted by provider]";
            var remaining = totalCap - used;
            var truncated = false;
            if (!binary && patch.Length > Math.Min(perFileCap, remaining))
            {
                patch = patch[..Math.Max(0, Math.Min(perFileCap, remaining))];
                truncated = true;
            }
            if (binary)
            {
                patch = "[binary file — patch omitted]";
            }
            else if (truncated)
            {
                patch += "\n[truncated]";
            }
            used += patch.Length;
            rows.Add(new
            {
                path = file.Path,
                previous_path = file.PreviousPath,
                status = file.Status,
                additions = file.Additions,
                deletions = file.Deletions,
                binary,
                patch_truncated = truncated,
                patch = Sanitize(patch),
            });
            if (used >= totalCap) break;
        }
        return new
        {
            repository = repo,
            number,
            path_filter = pathFilter,
            files_matched = filtered.Length,
            files_returned = rows.Count,
            total_patch_cap = totalCap,
            files = rows,
        };
    }

    /// <summary>Create a PR issue comment and audit the mutation.</summary>
    public async Task<object> CommentAsync(
        Guid conversationId, string? repository, int number, string body,
        string? idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("comment_body_required");
        }
        if (body.Length > 4_000)
        {
            throw new InvalidOperationException("comment_body_too_large");
        }
        var (conv, repo, provider, connection) = await ResolveAsync(
            conversationId, repository, ct);
        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{conversationId:N}:{repo}:{number}:{body}")))[..24]
            : idempotencyKey.Trim();
        var safeBody = Sanitize(body);
        var commentId = await provider.CreateIssueCommentAsync(
            connection, repo, number, safeBody, ct);
        if (audit is not null)
        {
            await audit.RecordAsync("scm.pr_comment",
                actorUserId: conv.CreatedByUserId,
                orgId: conv.OrgId,
                targetType: "app_conversation",
                targetId: conversationId.ToString(),
                metadata: new
                {
                    repository = repo,
                    pull_request = number,
                    comment_id = commentId,
                    idempotency_key = key,
                    tool = "github_comment_pull_request",
                },
                cancellationToken: ct);
        }
        return new
        {
            ok = true,
            repository = repo,
            number,
            comment_id = commentId,
            idempotency_key = key,
        };
    }

    private async Task<(Conversations.ConversationRecord Conv, string Repo,
        IScmProvider Provider, ScmConnection Connection)> ResolveAsync(
        Guid conversationId, string? repository, CancellationToken ct)
    {
        var conv = await db.Conversations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct)
            ?? throw new InvalidOperationException("conversation_not_found");
        var repos = conv.GetRepositories();
        var repo = Application.Conversations.MultiRepoLayout.ResolveRepo(
            repos, repository)
            ?? throw new InvalidOperationException("repository_not_selected");
        if (conv.OrgId is null)
        {
            throw new InvalidOperationException("scm_connection_not_found");
        }
        var connection = await connections.FindForRepositoryAsync(
            conv.OrgId.Value, ScmKind.GitHub, repo, ct)
            ?? throw new InvalidOperationException("scm_connection_not_found");
        var provider = providers.Get(connection.Kind)
            ?? throw new InvalidOperationException("scm_provider_unavailable");
        return (conv, repo, provider, connection);
    }

    private static string Sanitize(string? value) =>
        TokenPattern().Replace(SecretRedactor.Redact(value ?? ""), "***");

    [GeneratedRegex("(?:sk-[A-Za-z0-9_-]{8,}|gh[pousr]_[A-Za-z0-9_]{8,}|github_pat_[A-Za-z0-9_]{8,})", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    private static bool MatchesPath(ScmPullRequestFile file, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var f = filter.Trim();
        static bool Glob(string value, string pattern)
        {
            if (pattern.EndsWith("/**", StringComparison.Ordinal))
                return value.StartsWith(pattern[..^3], StringComparison.OrdinalIgnoreCase);
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
                return value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
        return Glob(file.Path, f)
            || file.Path.Contains(f, StringComparison.OrdinalIgnoreCase)
            || (file.PreviousPath?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}