using System.Text.RegularExpressions;

namespace Sandcastle.Client.Services;

/// <summary>SPEC-170 RF-004: tolerant parser for `@[label](type:id)` mentions.</summary>
public static partial class MentionFormat
{
    /// <summary>One parsed mention token.</summary>
    public sealed record Parsed(string Label, string Type, string Id, string Raw);

    /// <summary>Parses all structured mentions; malformed mention text is ignored.</summary>
    public static IReadOnlyList<Parsed> Parse(string text) =>
        MentionRegex().Matches(text).Select(m => new Parsed(
            m.Groups[1].Value, m.Groups[2].Value,
            m.Groups[3].Value, m.Value)).ToList();

    /// <summary>Display text with structured mentions reduced to labels.</summary>
    public static string DisplayText(string text) =>
        MentionRegex().Replace(text, "$1");

    /// <summary>Best-effort navigation target for a mention chip.</summary>
    public static string? Href(string type, string id) => type switch
    {
        "conversation" => $"/conversations/{id}",
        "skill" => "/customize?tab=skills",
        "automation" => "/automations",
        "knowledge" => "/knowledge",
        _ => null,
    };

    [GeneratedRegex(@"@\[([^\]]+)\]\(([^:]+):([^\)]+)\)")]
    private static partial Regex MentionRegex();
}