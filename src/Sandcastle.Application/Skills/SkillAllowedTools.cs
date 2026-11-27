using System.Text.Json;

using Sandcastle.Domain.Skills;

namespace Sandcastle.Application.Skills;

/// <summary>
/// SPEC-168: parsing/evaluation of skill <c>allowed-tools</c>. Null means
/// unrestricted (back-compat); when any active skill declares the list,
/// the effective allowance is the union of all declared lists.
/// </summary>
public static class SkillAllowedTools
{
    /// <summary>Parses YAML-ish frontmatter: inline array or comma string.</summary>
    public static IReadOnlyList<string> ParseFrontmatter(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal)
            && value.EndsWith("]", StringComparison.Ordinal))
        {
            value = value[1..^1];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim().Trim('"', '\''))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Serializes for persistence; null stays null (unrestricted).</summary>
    public static string? ToJson(IReadOnlyList<string>? tools) =>
        tools is null ? null : JsonSerializer.Serialize(tools);

    /// <summary>Reads persisted tools; malformed/missing JSON means unrestricted.</summary>
    public static IReadOnlyList<string>? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Union semantics: no declaring skill = unrestricted; otherwise a tool
    /// must appear in at least one declared list.
    /// </summary>
    public static bool Allows(
        IReadOnlyList<Skill> activeSkills, string toolName,
        out string? restrictingSkill)
    {
        var restricted = activeSkills
            .Where(s => s.AllowedTools is not null).ToList();
        if (restricted.Count == 0)
        {
            restrictingSkill = null;
            return true;
        }

        var allowed = restricted
            .SelectMany(s => s.AllowedTools!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Contains(toolName))
        {
            restrictingSkill = null;
            return true;
        }

        restrictingSkill = restricted[0].Name;
        return false;
    }
}