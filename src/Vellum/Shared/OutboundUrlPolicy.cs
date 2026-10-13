using System.Net;
using System.Net.Sockets;

namespace Vellum.Shared;

/// <summary>Resolves a host name to its addresses. Tests replace it; the app uses DNS.</summary>
public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken ct);

/// <summary>
/// The result of a check. <see cref="Addresses"/> holds the addresses that were checked, so a
/// caller that connects next can connect to exactly those. It is empty when the host is a
/// name and no resolution was needed or possible.
/// </summary>
public sealed record OutboundUrlVerdict(bool Allowed, string? Reason, IReadOnlyList<IPAddress> Addresses)
{
    public static OutboundUrlVerdict Refuse(string reason) => new(false, reason, []);
}

/// <summary>
/// The one place that decides whether the server may call a URL that a user supplied.
/// Settings validation calls <see cref="CheckAsync(string?, bool, bool, CancellationToken)"/>
/// when a URL is saved. The settings resolver calls <see cref="CheckWithoutResolving"/> on
/// every read, so a stricter instance policy applies at once. A caller that makes the request
/// calls <c>CheckAsync</c> again at request time and connects to the addresses in the verdict,
/// because a host name can resolve to a different address later.
/// </summary>
public sealed class OutboundUrlPolicy(HostResolver resolve)
{
    public const int MaxHostNameLength = 253;
    public const int MaxLabelLength = 63;

    private const string NotAUrl = "Must be an absolute http or https URL.";
    private const string HasCredentials = "Must not contain credentials.";
    private const string BadHostName =
        "The host name is not valid. A host name has at most 253 characters and each part between dots has 1 to 63.";
    private const string Private = "Must not point at this machine or a private network.";
    private const string NeedsHttps =
        "Must use https. Plain http is allowed only for an address on this machine or a private network, and only when the instance policy allows private network URLs.";
    private const string Unresolved = "The host name does not resolve.";

    public Task<OutboundUrlVerdict> CheckAsync(string? url, bool allowPrivateNetwork, CancellationToken ct = default) =>
        CheckAsync(url, allowPrivateNetwork, requireHttps: false, ct);

    /// <param name="requireHttps">
    /// True for a URL that receives a credential on an instance that is open to other
    /// networks. Plain http then passes only when every address of the host is private and
    /// <paramref name="allowPrivateNetwork"/> is true.
    /// </param>
    public async Task<OutboundUrlVerdict> CheckAsync(
        string? url, bool allowPrivateNetwork, bool requireHttps, CancellationToken ct = default)
    {
        if (Target.Parse(url, out var refusal) is not { } target)
            return OutboundUrlVerdict.Refuse(refusal!);
        if (target.Decide(allowPrivateNetwork, requireHttps, target.Literal is { } literal ? [literal] : null) is { } known)
            return known;

        IPAddress[] addresses;
        try
        {
            addresses = await resolve(target.Host, ct);
        }
        catch (SocketException)
        {
            return OutboundUrlVerdict.Refuse(Unresolved);
        }
        catch (ArgumentException)
        {
            // The resolver refuses a name that is not a valid DNS name.
            return OutboundUrlVerdict.Refuse(BadHostName);
        }

        return addresses.Length == 0
            ? OutboundUrlVerdict.Refuse(Unresolved)
            : target.Decide(allowPrivateNetwork, requireHttps, addresses)!;
    }

    /// <summary>
    /// The same rules with no DNS lookup. A URL whose host is a name that would need a lookup
    /// passes here with no addresses; the request-time check resolves it.
    /// </summary>
    public static OutboundUrlVerdict CheckWithoutResolving(string? url, bool allowPrivateNetwork, bool requireHttps)
    {
        if (Target.Parse(url, out var refusal) is not { } target)
            return OutboundUrlVerdict.Refuse(refusal!);
        return target.Decide(allowPrivateNetwork, requireHttps, target.Literal is { } literal ? [literal] : null)
               ?? new OutboundUrlVerdict(true, null, []);
    }

    /// <summary>
    /// Scheme, host and port in one canonical text, for example <c>https://api.github.com</c>
    /// or <c>http://localhost:11434</c>. A default port is left out. A stored credential is
    /// bound to this text.
    /// </summary>
    public static string OriginOf(Uri url)
    {
        if (!url.IsAbsoluteUri)
            throw new ArgumentException("An origin needs an absolute URL.", nameof(url));
        var host = url.HostNameType == UriHostNameType.IPv6 ? url.Host : url.IdnHost.ToLowerInvariant();
        return url.IsDefaultPort ? $"{url.Scheme}://{host}" : $"{url.Scheme}://{host}:{url.Port}";
    }

    /// <summary>The origin of a URL in text, or null when it is not an absolute http or https URL.</summary>
    public static string? OriginOf(string? url) => Target.Parse(url, out _) is { } target ? OriginOf(target.Url) : null;

    /// <summary>
    /// True only for an address that the public internet routes. False for every block in
    /// the IANA IPv4 and IPv6 special-purpose registries (loopback, private, link-local which
    /// holds the cloud metadata address, shared address space, protocol assignments, relay
    /// anycast, benchmarking, documentation, reserved), for multicast, for the Azure platform
    /// address, for any IPv6 address outside global unicast space, and for an IPv6 address
    /// that carries one of those IPv4 addresses.
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
                0 or 10 or 127 => false,                         // "this network", private, loopback
                100 when (b[1] & 0xC0) == 64 => false,           // 100.64.0.0/10 shared address space
                168 when b[1] == 63 && b[2] == 129 && b[3] == 16 => false, // Azure platform address
                169 when b[1] == 254 => false,                   // link-local, cloud metadata
                172 when (b[1] & 0xF0) == 16 => false,           // 172.16.0.0/12
                192 when b[1] == 0 && b[2] is 0 or 2 => false,   // IETF protocol assignments, TEST-NET-1
                192 when b[1] == 31 && b[2] == 196 => false,     // AS112
                192 when b[1] == 52 && b[2] == 193 => false,     // AMT
                192 when b[1] == 88 && b[2] == 99 => false,      // 6to4 relay anycast (deprecated)
                192 when b[1] == 168 => false,
                192 when b[1] == 175 && b[2] == 48 => false,     // AS112 direct delegation
                198 when (b[1] & 0xFE) == 18 => false,           // benchmarking
                198 when b[1] == 51 && b[2] == 100 => false,     // TEST-NET-2
                203 when b[1] == 0 && b[2] == 113 => false,      // TEST-NET-3
                >= 224 => false,                                 // multicast, reserved, broadcast
                _ => true,
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        // 64:ff9b::/96 NAT64 carries an IPv4 address. 64:ff9b:1::/48 is for local use and is
        // refused below with everything else outside global unicast space.
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            return IsPublic(new IPAddress(b.AsSpan(12, 4)));

        // Global unicast is 2000::/3. Everything else is refused: ::, ::1, the IPv4-compatible
        // form ::a.b.c.d, the IPv4-translated form ::ffff:0:a.b.c.d, 100::/64 discard,
        // 100:0:0:1::/64, 5f00::/16, fc00::/7 unique-local, fe80::/10 link-local, fec0::/10
        // site-local and ff00::/8 multicast.
        if ((b[0] & 0xE0) != 0x20)
            return false;

        // 2001::/23 IETF protocol assignments: Teredo, benchmarking, AMT, AS112, ORCHID
        // 2001:10::/28, ORCHIDv2 2001:20::/28, DRIP 2001:30::/28.
        if (b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xFE) == 0) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;    // 2001:db8::/32 documentation
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublic(new IPAddress(b.AsSpan(2, 4)));  // 2002::/16 6to4
        if (b[0] == 0x26 && b[1] == 0x20 && b[2] == 0 && b[3] == 0x4F && b[4] == 0x80 && b[5] == 0) return false; // 2620:4f:8000::/48 AS112
        if (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0) return false;              // 3fff::/20 documentation

        return true;
    }

    private sealed record Target(Uri Url, string Host, IPAddress? Literal, bool IsLocalName)
    {
        public static Target? Parse(string? url, out string? refusal)
        {
            refusal = NotAUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host))
                return null;

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                refusal = HasCredentials;
                return null;
            }

            string host;
            try
            {
                host = uri.IdnHost.TrimEnd('.');
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                // The international form of the name has a part that is too long or not valid.
                refusal = BadHostName;
                return null;
            }

            // Uri reads the decimal, octal, hex and short forms of an IPv4 address as an address.
            // The second parse catches any literal that Uri left as a name.
            IPAddress? literal = null;
            if ((uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(uri.DnsSafeHost, out literal))
                || IPAddress.TryParse(host, out literal))
            {
                refusal = null;
                return new Target(uri, host, literal, false);
            }

            if (host.Length is 0 or > MaxHostNameLength || host.Split('.').Any(label => label.Length is 0 or > MaxLabelLength))
            {
                refusal = BadHostName;
                return null;
            }

            refusal = null;
            var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
            return new Target(uri, host, null, local);
        }

        /// <summary>The verdict, or null when it depends on addresses that are not known yet.</summary>
        public OutboundUrlVerdict? Decide(bool allowPrivateNetwork, bool requireHttps, IReadOnlyList<IPAddress>? addresses)
        {
            var httpsNeeded = requireHttps && Url.Scheme == Uri.UriSchemeHttp;
            if (httpsNeeded && !allowPrivateNetwork)
                return OutboundUrlVerdict.Refuse(NeedsHttps);

            if (IsLocalName)
                return allowPrivateNetwork ? new OutboundUrlVerdict(true, null, []) : OutboundUrlVerdict.Refuse(Private);

            if (addresses is null)
                return allowPrivateNetwork && !httpsNeeded ? new OutboundUrlVerdict(true, null, []) : null;

            // One private address is enough to refuse: the connection could use any of them.
            if (!allowPrivateNetwork && !addresses.All(IsPublic))
                return OutboundUrlVerdict.Refuse(Private);
            // One public address is enough to refuse plain http for the same reason.
            if (httpsNeeded && addresses.Any(IsPublic))
                return OutboundUrlVerdict.Refuse(NeedsHttps);

            return new OutboundUrlVerdict(true, null, addresses);
        }
    }
}
