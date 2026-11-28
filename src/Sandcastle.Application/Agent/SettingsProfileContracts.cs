namespace Sandcastle.Application.Agent;

/// <summary>SPEC-180: provider connection catalog with official endpoints.</summary>
public sealed record ProviderConnectionDescriptor(
    string Id, string Label, string DefaultBaseUrl,
    bool RequiresBaseUrl, bool RequiresSecret);

/// <summary>SPEC-180 provider validation shared by API and tests.</summary>
public static class ProviderConnectionTypes
{
    private static readonly ProviderConnectionDescriptor[] All =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1", false, true),
        new("anthropic", "Anthropic", "https://api.anthropic.com/v1", false, true),
        new("google", "Google", "https://generativelanguage.googleapis.com/v1beta/openai", false, true),
        new("openai_compatible", "OpenAI-compatible", "", true, false),
        new("ollama", "Ollama", "http://localhost:11434", false, false),
    ];

    public static IReadOnlyList<ProviderConnectionDescriptor> Catalog => All;

    public static ProviderConnectionDescriptor? Find(string? provider) =>
        All.FirstOrDefault(p => string.Equals(
            p.Id, provider?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsValidBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return true;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp
            && uri.Host is "localhost" or "127.0.0.1" or "::1";
    }
}