using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sandcastle.Application.Web;
using Sandcastle.Infrastructure.Agent;
using Sandcastle.Infrastructure.Sandbox;
using Sandcastle.Sandboxing;

namespace Sandcastle.Infrastructure.Web;

/// <summary>
/// <see cref="IWebFetchService"/> — http(s) only, SSRF guard (DNS →
/// rejeita IPs privados/loopback/link-local), respeita a allowlist de
/// egress quando <c>EgressMode=allowlist</c>, strip markdown-lite,
/// saída passa pelo <c>SecretRedactor</c>.
/// </summary>
public sealed partial class WebFetchService(
    IHttpClientFactory httpFactory,
    IOptions<SandboxOptions> sandboxOptions,
    ILogger<WebFetchService> logger) : IWebFetchService
{
    /// <inheritdoc />
    public async Task<WebFetchResult> FetchAsync(
        string url, int maxChars, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new WebFetchResult(
                false, url, 0, null, "only http(s) URLs are allowed");
        }

        if (await SsrfGuard.IsBlockedAsync(uri, ct))
        {
            return new WebFetchResult(
                false, url, 0, null, "blocked by SSRF guard");
        }

        var opts = sandboxOptions.Value;
        if (opts.EgressMode.Equals("allowlist", StringComparison.OrdinalIgnoreCase)
            && !IsAllowed(uri.Host, opts.EgressAllow))
        {
            return new WebFetchResult(
                false, url, 0, null,
                $"host '{uri.Host}' not in the egress allowlist");
        }

        try
        {
            var http = httpFactory.CreateClient("web-fetch");
            using var res = await http.GetAsync(uri, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            var text = StripHtml(body);
            if (text.Length > maxChars)
            {
                text = text[..maxChars] + "\n[…truncated…]";
            }

            return new WebFetchResult(true, res.RequestMessage?.RequestUri
                ?.ToString() ?? url, (int)res.StatusCode,
                SecretRedactor.Redact(text), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "web_fetch falhou para {Url}", url);
            return new WebFetchResult(
                false, url, 0, null, ex.Message);
        }
    }

    /// <summary>host ∈ allowlist — match exato ou sufixo <c>.host</c>.</summary>
    internal static bool IsAllowed(string host, string? allow)
    {
        if (string.IsNullOrWhiteSpace(allow))
        {
            return false;
        }

        foreach (var raw in allow.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pat = raw.TrimStart('*').TrimStart('.');
            if (host.Equals(pat, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + pat, StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith('*')
                    && host.EndsWith(pat, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }


    /// <summary>markdown-lite: remove tags/scripts, colapsa whitespace.</summary>
    internal static string StripHtml(string html)
    {
        var s = ScriptRegex().Replace(html, " ");
        s = TagRegex().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = BlankLineRegex().Replace(s, "\n");
        s = SpaceRegex().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s*\\n\\s*")]
    private static partial Regex BlankLineRegex();

    [GeneratedRegex("[ \\t]{2,}")]
    private static partial Regex SpaceRegex();
}