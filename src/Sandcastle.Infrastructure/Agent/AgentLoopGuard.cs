using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sandcastle.Infrastructure.Agent;

/// <summary>Verdict of one agent-loop guard registration.</summary>
public enum LoopVerdict
{
    Ok,
    VetoSameTool,
    VetoRepeatArgs,
}

/// <summary>
/// SPEC-164 RF-001: per-run repeated-tool guard. The same tool called
/// consecutively is capped separately from the exact same tool+canonical-args
/// call; switching tools resets both streaks.
/// </summary>
public sealed class AgentLoopGuard(int maxSameToolConsecutive = 6, int maxRepeatArgs = 3)
{
    private readonly int _maxSameTool = Math.Max(2, maxSameToolConsecutive);
    private readonly int _maxRepeatArgs = Math.Max(2, maxRepeatArgs);
    private string? _lastTool;
    private string? _lastArgsHash;
    private int _sameToolStreak;
    private int _repeatArgsStreak;

    /// <summary>Registers a proposed call and returns whether it must be vetoed.</summary>
    public LoopVerdict Register(string toolName, string? argumentsJson)
    {
        var argsHash = CanonicalArgsHash(argumentsJson);
        if (toolName == _lastTool)
        {
            _sameToolStreak++;
            _repeatArgsStreak = argsHash == _lastArgsHash
                ? _repeatArgsStreak + 1 : 1;
        }
        else
        {
            _sameToolStreak = 1;
            _repeatArgsStreak = 1;
        }

        _lastTool = toolName;
        _lastArgsHash = argsHash;
        if (_repeatArgsStreak >= _maxRepeatArgs)
        {
            return LoopVerdict.VetoRepeatArgs;
        }

        return _sameToolStreak >= _maxSameTool
            ? LoopVerdict.VetoSameTool : LoopVerdict.Ok;
    }

    /// <summary>SHA-256 of a canonical JSON representation (object keys sorted recursively).</summary>
    public static string CanonicalArgsHash(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            argumentsJson = "{}";
        }

        byte[] canonical;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                WriteCanonical(doc.RootElement, writer);
            }

            canonical = output.ToArray();
        }
        catch (JsonException)
        {
            canonical = Encoding.UTF8.GetBytes(argumentsJson);
        }

        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in value.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(prop.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}