using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Modules.Ai;
using Vellum.Modules.Ai.Providers;
using Vellum.Modules.GitHub;
using Vellum.Shared;
using Vellum.Shared.Outbound;

namespace Vellum.Tests.Shared.Outbound;

/// <summary>The outbound pipeline by itself: the URL policy at request time, the pinned connection and the retry rule.</summary>
public class OutboundPipelineTests
{
    private const string PublicAddress = "93.184.216.34";

    private static ServiceProvider Build(HostResolver resolver, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(resolver);
        services.AddSingleton<OutboundUrlPolicy>();
        services.AddSingleton(OutboundLimits.Default with { Delay = (_, _) => Task.CompletedTask, Jitter = () => 0 });
        configure?.Invoke(services);
        services.AddOutbound();
        services.AddOutboundClient<IGitHubApi>();
        services.AddOutboundClient<IAnthropicApi>();
        return services.BuildServiceProvider();
    }

    private static OutboundCall Call(string baseUrl, bool allowPrivateNetwork) =>
        new("github", "rate-limit", new Uri(baseUrl), allowPrivateNetwork, RequireHttps: false, TimeSpan.FromSeconds(60))
        {
            Credential = new OutboundCredential("Authorization", "Bearer", "pipeline-test-secret"),
        };

    [Theory]
    [InlineData("https://internal.example", "10.0.0.5")]
    [InlineData("https://metadata.example", "169.254.169.254")]
    [InlineData("https://mixed.example", "93.184.216.34,192.168.1.10")]
    [InlineData("http://127.0.0.1:5432", null)]
    [InlineData("http://[::1]:8080", null)]
    [InlineData("http://localhost:5195", "127.0.0.1")]
    public async Task A_hosted_instance_refuses_a_base_url_that_reaches_a_private_address(string baseUrl, string? resolvesTo)
    {
        var stub = new StubTransport().Json("{}");
        await using var provider = Build(
            (_, _) => Task.FromResult(resolvesTo!.Split(',').Select(IPAddress.Parse).ToArray()),
            services => services.AddSingleton<OutboundPrimaryHandlerFactory>(_ => () => stub));
        var api = provider.GetRequiredService<IGitHubApi>();

        var refused = await Assert.ThrowsAsync<OutboundException>(() => OutboundSend.RunAsync(() => api.GetRateLimit(Call(baseUrl, allowPrivateNetwork: false), default)));

        Assert.Equal(OutboundFailure.UrlRefused, refused.Failure);
        Assert.Empty(stub.Requests);
        Assert.DoesNotContain("pipeline-test-secret", refused.ToString());

        // The same address is allowed where the instance policy allows private networks.
        using var allowed = await api.GetRateLimit(Call(baseUrl, allowPrivateNetwork: true), default);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task The_address_is_checked_again_for_a_retry()
    {
        var lookups = 0;
        var stub = new StubTransport().Json(HttpStatusCode.ServiceUnavailable, "{}").Json("{}");
        await using var provider = Build(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(Interlocked.Increment(ref lookups) == 1 ? PublicAddress : "10.0.0.5") }),
            services => services.AddSingleton<OutboundPrimaryHandlerFactory>(_ => () => stub));

        var refused = await Assert.ThrowsAsync<OutboundException>(() =>
            OutboundSend.RunAsync(() => provider.GetRequiredService<IGitHubApi>().GetRateLimit(Call("https://rebind.example", allowPrivateNetwork: false), default)));

        Assert.Equal(OutboundFailure.UrlRefused, refused.Failure);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task The_connection_goes_to_the_checked_address_when_the_name_changes_its_answer()
    {
        // A local listener stands in for the public server. The dialer records where the
        // pipeline wanted to connect and connects to the listener instead.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                request.Append(Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer)));
            const string body = """{"resources":{"core":{"limit":60,"remaining":59,"reset":1767225600}}}""";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            return request.ToString();
        });

        var lookups = 0;
        var dialed = new List<IPEndPoint>();
        await using var provider = Build(
            // The first answer is public. Every later answer points at this machine.
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(Interlocked.Increment(ref lookups) == 1 ? PublicAddress : "127.0.0.1") }),
            services => services.AddSingleton<SocketDialer>((endpoint, ct) =>
            {
                dialed.Add(endpoint);
                return PinnedConnector.DialSocketAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
            }));

        using var response = await provider.GetRequiredService<IGitHubApi>()
            .GetRateLimit(Call("http://rebind.example:8080", allowPrivateNetwork: false), default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([new IPEndPoint(IPAddress.Parse(PublicAddress), 8080)], dialed);
        // The name was resolved once, for the check. The connection did not ask again.
        Assert.Equal(1, lookups);
        var received = await serving;
        Assert.Contains("GET /rate_limit HTTP/1.1", received);
        Assert.Contains("Host: rebind.example:8080", received);
        Assert.Contains("Authorization: Bearer pipeline-test-secret", received);
    }

    [Fact]
    public async Task A_write_is_not_sent_again_after_a_502_and_a_read_is()
    {
        var stub = new StubTransport().Json(HttpStatusCode.BadGateway, "{}").Json("{}");
        await using var provider = Build(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(PublicAddress) }),
            services => services.AddSingleton<OutboundPrimaryHandlerFactory>(_ => () => stub));

        using var write = await provider.GetRequiredService<IAnthropicApi>().CreateMessage(
            new JsonObject { ["model"] = "m" }, Call("https://api.example", allowPrivateNetwork: false), default);
        Assert.Equal(HttpStatusCode.BadGateway, write.StatusCode);
        Assert.Single(stub.Requests);

        var second = new StubTransport().Json(HttpStatusCode.BadGateway, "{}").Json("{}");
        await using var reads = Build(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(PublicAddress) }),
            services => services.AddSingleton<OutboundPrimaryHandlerFactory>(_ => () => second));
        using var read = await reads.GetRequiredService<IGitHubApi>().GetRateLimit(Call("https://api.example", allowPrivateNetwork: false), default);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(2, second.Requests.Count);
    }

    [Fact]
    public void A_credential_and_a_call_print_no_secret()
    {
        var call = Call("https://api.example", allowPrivateNetwork: false);

        Assert.DoesNotContain("pipeline-test-secret", call.ToString());
        Assert.DoesNotContain("pipeline-test-secret", call.Credential!.ToString());
        Assert.DoesNotContain("pipeline-test-secret", new OutboundException(OutboundFailure.Timeout, call).ToString());
    }

    [Theory]
    [InlineData("export GITHUB_TOKEN=ghp_0123456789abcdefghijABCDEFGHIJ012345", "ghp_0123456789")]
    [InlineData("OPENAI_API_KEY: sk-proj-abcdefghijklmnopqrstuvwxyz012345", "sk-proj-abcdefghij")]
    [InlineData("{\"password\": \"correct horse battery\"}", "correct horse battery")]
    [InlineData("Server=db;Database=orders;User Id=sa;Password=Sup3rS3cret!;", "Sup3rS3cret!")]
    [InlineData("postgres://orders:p4ssw0rd@db.internal:5432/orders", "p4ssw0rd")]
    [InlineData("Authorization: Bearer abcdef0123456789abcdef", "abcdef0123456789abcdef")]
    [InlineData("x-api-key: 9f8e7d6c5b4a39281706f5e4d3c2b1a0", "9f8e7d6c5b4a3928")]
    [InlineData("client_secret = 'a1b2c3d4e5f6g7h8'", "a1b2c3d4e5f6g7h8")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----", "b3BlbnNzaC1rZXktdjEAAAAA")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC", "MIIEvQIBADANBgkqhkiG9w0BAQEFAASC")]
    [InlineData("aws_secret_access_key=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "wJalrXUtnFEMI")]
    public void The_redactor_removes_credentials(string text, string secret)
    {
        var redacted = SecretRedactor.Redact(text);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains(SecretRedactor.Marker, redacted);
    }

    [Theory]
    [InlineData("The OrderCreated event has fields orderId and total.")]
    [InlineData("maxTokens = 500; retries: 3")]
    [InlineData("GET https://api.example.com/orders/42?expand=lines")]
    public void The_redactor_leaves_ordinary_text(string text) => Assert.Equal(text, SecretRedactor.Redact(text));
}
