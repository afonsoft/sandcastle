using System.Net;
using System.Net.Sockets;

namespace Sandcastle.Infrastructure.Web;

/// <summary>
/// SPEC-158: guard SSRF compartilhado — resolve o host e rejeita
/// endereços privados/loopback/link-local (fail-closed em erro de DNS).
/// Extraído de <see cref="WebFetchService"/> para reuso por webhooks
/// de notificação de usuário.
/// </summary>
internal static class SsrfGuard
{
    /// <summary>true se o URI resolve para endereço privado/interno.</summary>
    public static async Task<bool> IsBlockedAsync(Uri uri, CancellationToken ct)
    {
        if (uri.HostNameType is UriHostNameType.IPv4
            or UriHostNameType.IPv6)
        {
            return IsPrivate(IPAddress.Parse(uri.Host));
        }

        IPAddress[] addrs;
        try
        {
            addrs = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch (Exception)
        {
            return true; // DNS failure — trate como bloqueado (fail-closed).
        }

        return addrs.Length == 0 || addrs.Any(IsPrivate);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal
            || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] is 10 or 127 or 0
                || b[0] == 169 && b[1] == 254          // link-local
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 100 && b[1] is >= 64 and <= 127; // CGNAT
        }

        // IPv6: fc00::/7 (ULA), fe80::/10 já coberto acima, ::1 loopback.
        return ip.IsIPv6SiteLocal || b[0] is 0xFC or 0xFD;
    }
}