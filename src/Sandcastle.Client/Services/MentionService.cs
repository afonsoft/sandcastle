using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sandcastle.Client.Services;

/// <summary>
/// SPEC-170 RF-001/002: client-side mention aggregation, cache and ranking.
/// Files arrive from the workspace provider; other types use `/api/v1/mentions`.
/// </summary>
public sealed class MentionService(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, IReadOnlyList<MentionItem>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _pending;

    /// <summary>One rich mention suggestion.</summary>
    public sealed record MentionItem(
        string Type, string Id, string Label,
        string? Snippet = null, string? Icon = null, int Score = 0);

    /// <summary>Server response item DTO.</summary>
    public sealed record MentionDto(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("snippet")] string? Snippet,
        [property: JsonPropertyName("icon")] string? Icon);

    private sealed record MentionPage(
        [property: JsonPropertyName("items")] List<MentionDto>? Items);

    /// <summary>Searches server-side mention providers with cache + cancellation.</summary>
    public async Task<IReadOnlyList<MentionItem>> SearchServerAsync(
        string query, string types = "conversation,skill,automation,knowledge",
        int limit = 20, CancellationToken ct = default)
    {
        var key = $"{types}|{limit}|{query.Trim()}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _pending?.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pending = linked;
        var url = "api/v1/mentions?q=" + Uri.EscapeDataString(query)
            + "&types=" + Uri.EscapeDataString(types)
            + "&limit=" + limit;
        var page = await http.GetFromJsonAsync<MentionPage>(url, Json, linked.Token)
            ?? new MentionPage([]);
        var items = (page.Items ?? []).Select(x =>
            new MentionItem(x.Type, x.Id, x.Label, x.Snippet, x.Icon))
            .ToList();
        if (_cache.Count > 100)
        {
            _cache.Clear();
        }

        _cache[key] = items;
        return items;
    }

    /// <summary>Creates file mention candidates from workspace paths.</summary>
    public static IEnumerable<MentionItem> FileItems(
        IEnumerable<string> paths, string query)
    {
        foreach (var path in paths)
        {
            var score = IdeQuickOpenFuzzy.Score(query, path);
            if (score > 0)
            {
                yield return new MentionItem("file", path, path,
                    null, "file", score);
            }
        }
    }

    /// <summary>Ranks merged mentions: prefix beats substring; provider scores break ties.</summary>
    public static IReadOnlyList<MentionItem> Rank(
        IEnumerable<MentionItem> items, string query, int take = 100) =>
        items.Select(i => i with { Score = Score(i, query) + i.Score })
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.Type, StringComparer.Ordinal)
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(take).ToList();

    private static int Score(MentionItem item, string query) =>
        item.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1000
        : item.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ? 500
        : item.Snippet?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ? 100 : 0;
}