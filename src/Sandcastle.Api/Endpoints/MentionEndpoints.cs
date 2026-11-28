using Microsoft.EntityFrameworkCore;

using Sandcastle.Application.Identity;
using Sandcastle.Infrastructure.Persistence;

namespace Sandcastle.Api.Endpoints;

/// <summary>SPEC-170 RF-005: lightweight mention search for composer.</summary>
public static class MentionEndpoints
{
    private static readonly string[] KnownTypes =
        ["conversation", "skill", "automation", "knowledge"];

    /// <summary>Maps mention endpoints.</summary>
    public static IEndpointRouteBuilder MapMentionEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mentions", SearchAsync)
            .WithTags("Mentions").RequireAuthorization();
        return app;
    }

    private static async Task<IResult> SearchAsync(
        string? q, string? types, int? limit,
        AppDbContext db, ICurrentUserAccessor current,
        CancellationToken ct)
    {
        var query = (q ?? "").Trim();
        if (query.Length == 0)
        {
            return Results.Ok(new { items = Array.Empty<object>() });
        }

        var requested = (types ?? "").Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => KnownTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            requested.UnionWith(KnownTypes);
        }

        var take = Math.Clamp(limit ?? 20, 1, 50);
        var userId = current.User?.UserId.ToString();
        var items = new List<MentionItem>();

        if (requested.Contains("conversation"))
        {
            var rows = await db.Conversations.AsNoTracking()
                .OrderByDescending(c => c.CreatedAtTicks)
                .Take(200).ToListAsync(ct);
            items.AddRange(rows
                .Where(c => c.ExecutionStatus != "Archived")
                .Where(c => userId is null || c.CreatedByUserId is null
                    || c.CreatedByUserId == userId)
                .Where(c => Matches(c.Title, query)
                    || Matches(c.SelectedRepository, query))
                .Select(c => new MentionItem("conversation", c.Id.ToString(),
                    c.Title ?? c.SelectedRepository ?? c.Id.ToString()[..8],
                    c.SelectedRepository, "message-circle")));
        }

        if (requested.Contains("skill"))
        {
            var rows = await db.Skills.AsNoTracking().Take(200).ToListAsync(ct);
            items.AddRange(rows
                .Where(s => s.Enabled)
                .Where(s => Matches(s.Name, query)
                    || Matches(s.Description, query))
                .Select(s => new MentionItem("skill", s.Id.ToString(),
                    s.Name, s.Description, "sparkles")));
        }

        if (requested.Contains("automation"))
        {
            var rows = await db.Automations.AsNoTracking()
                .Take(200).ToListAsync(ct);
            items.AddRange(rows
                .Where(a => userId is null || a.CreatedByUserId == userId)
                .Where(a => Matches(a.Name, query) || Matches(a.Prompt, query))
                .Select(a => new MentionItem("automation", a.Id.ToString(),
                    a.Name, a.SelectedRepository ?? a.ScheduleKind, "clock")));
        }

        if (requested.Contains("knowledge"))
        {
            var memoryRows = await db.MemoryRecords.AsNoTracking()
                .Take(200).ToListAsync(ct);
            items.AddRange(memoryRows
                .Where(m => m.DeletedAt is null)
                .Where(m => userId is null || m.CreatedByUserId is null
                    || m.CreatedByUserId == userId)
                .Where(m => Matches(m.Key, query) || Matches(m.Text, query))
                .Select(m => new MentionItem("knowledge", m.Id.ToString(),
                    m.Key, Snippet(m.Text), "book")));

            var rows = await db.KnowledgeSuggestions.AsNoTracking()
                .Take(200).ToListAsync(ct);
            items.AddRange(rows
                .Where(k => k.Status == "approved")
                .Where(k => Matches(k.Title, query) || Matches(k.Content, query))
                .Select(k => new MentionItem("knowledge", k.Id.ToString(),
                    k.Title, Snippet(k.Content), "book")));

            var collectionRows = await db.KnowledgeCollections.AsNoTracking()
                .Take(200).ToListAsync(ct);
            items.AddRange(collectionRows
                .Where(c => userId is null || c.OwnerUserId == userId)
                .Where(c => Matches(c.Name, query) || Matches(c.Description, query) || Matches(c.RepoPath, query))
                .Select(c => new MentionItem("knowledge", c.Id.ToString(),
                    c.Name, Snippet(c.Description ?? c.RepoPath ?? "collection"), "book")));
        }

        return Results.Ok(new
        {
            items = items
                .OrderByDescending(i => Score(i, query))
                .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .Select(i => new
                {
                    type = i.Type,
                    id = i.Id,
                    label = i.Label,
                    snippet = i.Snippet,
                    icon = i.Icon,
                }).ToList(),
        });
    }

    private static bool Matches(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static string Snippet(string value) =>
        value.Length <= 160 ? value : value[..160] + "…";

    private static int Score(MentionItem item, string query) =>
        item.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 2
        : item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private sealed record MentionItem(
        string Type, string Id, string Label, string? Snippet, string Icon);
}