using System.Text.RegularExpressions;

namespace Sandcastle.Infrastructure.Agent;

/// <summary>SPEC-193 RF-005: advisor severity normalization.</summary>
public static partial class AdvisorSeverityParser
{
    /// <summary>Normalizes free-form advisor output to nit|concern|blocker.</summary>
    public static string Normalize(string? answer)
    {
        var text = answer ?? "";
        var match = SeverityPattern().Match(text);
        if (match.Success) return match.Groups[1].Value.ToLowerInvariant();
        if (BlockerPattern().IsMatch(text)) return "blocker";
        if (NitPattern().IsMatch(text)) return "nit";
        return "concern";
    }

    [GeneratedRegex(@"(?:severity\s*[:\-]\s*|^\s*\[?)(nit|concern|blocker)(?:\]?\s*[:\-])?", RegexOptions.IgnoreCase)]
    private static partial Regex SeverityPattern();

    [GeneratedRegex(@"\bblocker\b|\bmust not\b|\bdo not proceed\b", RegexOptions.IgnoreCase)]
    private static partial Regex BlockerPattern();

    [GeneratedRegex(@"\bnit\b|^\s*OK\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NitPattern();
}