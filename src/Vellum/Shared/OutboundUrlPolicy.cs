using System.Net;
using System.Net.Sockets;

namespace Vellum.Shared;

/// <summary>Resolves a host name to its addresses. Tests replace it; the app uses DNS.</summary>
public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken ct);

/// <summary>
/// The result of a check. <see cref="Addresses"/> holds the addresses that were checked, so a
/// caller that connects next can connect to exactly those. It is empty when private networks
/// are allowed, because no resolution is done then.
/// </summary>
public sealed record OutboundUrlVerdict(bool Allowed, string? Reason, IReadOnlyList<IPAddress> Addresses)
{
    public static OutboundUrlVerdict Refuse(string reason) => new(false, reason, []);
}

/// <summary>
/// The one place that decides whether the server may call a URL that a user supplied.
/// Settings validation calls it when a URL is saved. A caller that makes the request calls it
/// again at request time, because a host name can resolve to a different address later.
/// </summary>
public sealed class OutboundUrlPolicy(HostResolver resolve)
{
    public async Task<OutboundUrlVerdict> CheckAsync(string? url, bool allowPrivateNetwork, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
            return OutboundUrlVerdict.Refuse("Must be an absolute http or https URL.");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return OutboundUrlVerdict.Refuse("Must not contain credentials.");

        if (allowPrivateNetwork)
            return new OutboundUrlVerdict(true, null, []);

        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return OutboundUrlVerdict.Refuse("Must not point at this machine or a private network.");

        IPAddress[] addresses;
        // Uri reads the decimal, octal, hex and short forms of an IPv4 address as an address.
        // The second parse catches any literal that Uri left as a name.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(uri.DnsSafeHost, out var literal))
        {
            addresses = [literal];
        }
        else if (IPAddress.TryParse(host, out literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolve(host, ct);
            }
            catch (SocketException)
            {
                return OutboundUrlVerdict.Refuse("The host name does not resolve.");
            }

            if (addresses.Length == 0)
                return OutboundUrlVerdict.Refuse("The host name does not resolve.");
        }

        // One private address is enough to refuse: the connection could use any of them.
        return addresses.All(IsPublic)
            ? new OutboundUrlVerdict(true, null, addresses)
            : OutboundUrlVerdict.Refuse("Must not point at this machine or a private network.");
    }

    /// <summary>
    /// False for loopback, link-local (which holds the cloud metadata address), private,
    /// unique-local, carrier-grade NAT, multicast, documentation and reserved ranges, and for
    /// an IPv6 address that carries one of those IPv4 addresses.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsPublic(address.MapToIPv4());

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] switch
            {
                0 or 10 or 127 => false,
                100 when (b[1] & 0xC0) == 64 => false,          // 100.64.0.0/10 carrier-grade NAT
                169 when b[1] == 254 => false,                  // link-local, cloud metadata
                172 when (b[1] & 0xF0) == 16 => false,          // 172.16.0.0/12
                192 when b[1] == 168 => false,
                192 when b[1] == 0 && b[2] is 0 or 2 => false,  // IETF protocol assignments, TEST-NET-1
                198 when (b[1] & 0xFE) == 18 => false,          // benchmarking
                198 when b[1] == 51 && b[2] == 100 => false,    // TEST-NET-2
                203 when b[1] == 0 && b[2] == 113 => false,     // TEST-NET-3
                >= 224 => false,                                // multicast, reserved, broadcast
                _ => true,
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        if (b[0] == 0xFF) return false;                              // multicast
        if ((b[0] & 0xFE) == 0xFC) return false;                     // fc00::/7 unique-local
        if (b[0] == 0xFE && (b[1] & 0x80) == 0x80) return false;     // fe80::/10 link-local, fec0::/10 site-local
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;   // 2001:db8::/32 documentation
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) return false;         // 2001::/32 Teredo
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublic(new IPAddress(b.AsSpan(2, 4))); // 2002::/16 6to4

        // 64:ff9b::/96 NAT64 carries an IPv4 address; 64:ff9b:1::/48 is for local use.
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)
            return b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0 && IsPublic(new IPAddress(b.AsSpan(12, 4)));

        // ::, ::1 and the IPv4-compatible form ::a.b.c.d.
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            return false;

        return true;
    }
}
