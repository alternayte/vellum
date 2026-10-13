using System.Net;
using System.Net.Sockets;
using Vellum.Shared;

namespace Vellum.Tests.Shared;

public class OutboundUrlPolicyTests
{
    // No test reaches DNS: every name the tests use is in this table.
    private static readonly OutboundUrlPolicy Policy = new((host, _) => host switch
    {
        "public.example" => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }),
        "public6.example" => Task.FromResult(new[] { IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946") }),
        "internal.example" => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }),
        "metadata.example" => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }),
        "mixed.example" => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("192.168.1.10") }),
        "internal6.example" => Task.FromResult(new[] { IPAddress.Parse("fd12:3456:789a::1") }),
        "empty.example" => Task.FromResult(Array.Empty<IPAddress>()),
        _ => throw new SocketException((int)SocketError.HostNotFound),
    });

    [Theory]
    [InlineData("https://public.example")]
    [InlineData("http://public.example:8443/v1")]
    [InlineData("https://public6.example/path?x=1")]
    [InlineData("https://93.184.216.34/")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/")]
    public async Task A_public_http_url_is_allowed(string url)
    {
        var verdict = await Policy.CheckAsync(url, allowPrivateNetwork: false);
        Assert.True(verdict.Allowed, verdict.Reason);
        Assert.NotEmpty(verdict.Addresses);
    }

    [Theory]
    // Not an absolute http(s) URL.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("public.example")]
    [InlineData("/relative/path")]
    [InlineData("ftp://public.example")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://public.example")]
    // Credentials in the URL.
    [InlineData("https://user:pass@public.example")]
    [InlineData("https://token@public.example")]
    // Loopback, by name and by literal.
    [InlineData("http://localhost")]
    [InlineData("http://LOCALHOST:5432")]
    [InlineData("http://localhost./")]
    [InlineData("http://api.localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.8.9.10:8080")]
    [InlineData("http://0.0.0.0")]
    // Private, carrier-grade NAT, link-local and metadata IPv4.
    [InlineData("http://10.0.0.1")]
    [InlineData("http://172.16.0.1")]
    [InlineData("http://172.31.255.254")]
    [InlineData("http://192.168.0.1")]
    [InlineData("http://100.64.0.1")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://169.254.0.1")]
    [InlineData("http://224.0.0.1")]
    [InlineData("http://255.255.255.255")]
    // Decimal, hex, octal and short forms of 127.0.0.1, 10.0.0.1 and the metadata address.
    [InlineData("http://2130706433")]
    [InlineData("http://0x7f000001")]
    [InlineData("http://0x7f.0.0.1")]
    [InlineData("http://0177.0.0.1")]
    [InlineData("http://127.1")]
    [InlineData("http://167772161")]
    [InlineData("http://0xA9FEA9FE")]
    [InlineData("http://2852039166")]
    // IPv6 loopback, unspecified, link-local, unique-local, site-local, multicast.
    [InlineData("http://[::1]")]
    [InlineData("http://[::]")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:8080")]
    [InlineData("http://[fe80::1]")]
    [InlineData("http://[fc00::1]")]
    [InlineData("http://[fd00:ec2::254]")]
    [InlineData("http://[fec0::1]")]
    [InlineData("http://[ff02::1]")]
    // IPv6 forms that carry a private IPv4 address.
    [InlineData("http://[::ffff:127.0.0.1]")]
    [InlineData("http://[::ffff:7f00:1]")]
    [InlineData("http://[::ffff:10.0.0.1]")]
    [InlineData("http://[::ffff:169.254.169.254]")]
    [InlineData("http://[::127.0.0.1]")]
    [InlineData("http://[64:ff9b::10.0.0.1]")]
    [InlineData("http://[64:ff9b:1::1]")]
    [InlineData("http://[2002:7f00:1::1]")]
    [InlineData("http://[2002:c0a8:101::1]")]
    [InlineData("http://[2001:db8::1]")]
    [InlineData("http://[2001:0:4136:e378:8000:63bf:3fff:fdd2]")]
    // A name that resolves to a private address, wholly or in part, or to nothing.
    [InlineData("https://internal.example")]
    [InlineData("https://internal6.example")]
    [InlineData("https://metadata.example/computeMetadata/v1/")]
    [InlineData("https://mixed.example")]
    [InlineData("https://empty.example")]
    [InlineData("https://unknown.example")]
    public async Task A_url_that_is_malformed_or_reaches_a_private_network_is_refused(string? url)
    {
        var verdict = await Policy.CheckAsync(url, allowPrivateNetwork: false);
        Assert.False(verdict.Allowed, url);
        Assert.False(string.IsNullOrEmpty(verdict.Reason));
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://192.168.1.20:1234/v1")]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://ollama.internal:11434")]
    public async Task A_private_address_is_allowed_when_the_policy_allows_private_networks(string url)
    {
        // No resolution happens here: "ollama.internal" is not in the resolver table.
        Assert.True((await Policy.CheckAsync(url, allowPrivateNetwork: true)).Allowed);
    }

    [Theory]
    [InlineData("ftp://localhost")]
    [InlineData("http://user:pass@localhost")]
    [InlineData("localhost:11434")]
    public async Task Scheme_and_credential_rules_hold_when_private_networks_are_allowed(string url)
    {
        Assert.False((await Policy.CheckAsync(url, allowPrivateNetwork: true)).Allowed);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.15.255.255", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("100.128.0.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("64:ff9b::8.8.8.8", true)]
    [InlineData("2002:808:808::1", true)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.7", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("240.0.0.1", false)]
    public void Address_ranges_are_classified(string address, bool isPublic)
    {
        Assert.Equal(isPublic, OutboundUrlPolicy.IsPublic(IPAddress.Parse(address)));
    }
}
