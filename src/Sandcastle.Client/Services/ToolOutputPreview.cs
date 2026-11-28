using System.Text.Json;

namespace Sandcastle.Client.Services;

/// <summary>SPEC-193 RF-006: bounded observation preview preserving line-oriented diffs.</summary>
public static class ToolOutputPreview
{
    /// <summary>Creates a readable preview, preserving +/- diff lines instead of flattening them.</summary>
    public static string Preview(string text, int maxLines = 4, int maxChars = 420)
    {
        var normalized = (text ?? "").Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0) return "";
        var lines = normalized.Split('\n');
        var diff = lines.Any(l => l.StartsWith('+') || l.StartsWith('-') || l.StartsWith("@@"));
        var picked = diff
            ? lines.Where(l => l.StartsWith('+') || l.StartsWith('-') || l.StartsWith("@@"))
                .Take(maxLines).ToArray()
            : lines.Take(maxLines).ToArray();
        var result = string.Join('\n', picked);
        if (lines.Length > picked.Length) result += "\n…";
        return result.Length <= maxChars ? result : result[..maxChars] + "…";
    }

    /// <summary>Interpolates machine-readable counts/paths when present in a JSON observation.</summary>
    public static string? Summary(JsonElement observation)
    {
        if (observation.ValueKind == JsonValueKind.String)
            return Preview(observation.GetString() ?? "", 2, 160);
        if (observation.ValueKind != JsonValueKind.Object) return null;
        var parts = new List<string>();
        if (observation.TryGetProperty("count", out var count)
            && count.ValueKind == JsonValueKind.Number) parts.Add($"{count.GetInt32()} items");
        if (observation.TryGetProperty("pages_read", out var pages)
            && pages.ValueKind == JsonValueKind.Number) parts.Add($"{pages.GetInt32()} pages");
        if (observation.TryGetProperty("rows_returned", out var rows)
            && rows.ValueKind == JsonValueKind.Number) parts.Add($"{rows.GetInt32()} rows");
        foreach (var field in new[] { "path", "archive", "job_id", "key" })
        {
            if (observation.TryGetProperty(field, out var p)
                && p.ValueKind == JsonValueKind.String) parts.Add(p.GetString()!);
        }
        if (parts.Count > 0) return string.Join(" · ", parts.Take(3));
        foreach (var field in new[] { "output", "diff", "patch", "text" })
        {
            if (observation.TryGetProperty(field, out var output)
                && output.ValueKind == JsonValueKind.String)
            {
                return Preview(output.GetString() ?? "", 4, 160);
            }
        }
        return null;
    }
}