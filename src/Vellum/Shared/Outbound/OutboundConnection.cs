using System.Net;
using System.Net.Sockets;

namespace Vellum.Shared.Outbound;

/// <summary>Opens a TCP connection to one address. Tests replace it; the app uses a socket.</summary>
public delegate ValueTask<Stream> SocketDialer(IPEndPoint endpoint, CancellationToken ct);

/// <summary>Makes the transport under the delegating handlers. Tests replace it with a stub that serves fixtures.</summary>
public delegate HttpMessageHandler OutboundPrimaryHandlerFactory();

/// <summary>
/// Connects to the addresses that <see cref="OutboundUrlPolicy"/> checked for this request and
/// to no other. The host name is not resolved a second time, so a name that changes its
/// answer after the check (DNS rebinding) cannot move the connection.
/// </summary>
public sealed class PinnedConnector(HostResolver resolve, SocketDialer dial)
{
    public static async ValueTask<Stream> DialSocketAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool publicOnly, CancellationToken ct)
    {
        var call = OutboundCall.From(context.InitialRequestMessage);
        context.InitialRequestMessage.Options.TryGetValue(OutboundCall.PinnedAddressesKey, out var pinned);

        IReadOnlyList<IPAddress> addresses;
        if (pinned is { Count: > 0 })
            addresses = pinned;
        else if (publicOnly)
            throw new OutboundException(OutboundFailure.UrlRefused, call, "The destination has no checked address.");
        else if (IPAddress.TryParse(context.DnsEndPoint.Host, out var literal))
            addresses = [literal];
        else
            addresses = await resolve(context.DnsEndPoint.Host, ct);

        // The check already refused these. This is the second lock on the same door.
        if (publicOnly && !addresses.All(OutboundUrlPolicy.IsPublic))
            throw new OutboundException(OutboundFailure.UrlRefused, call, "Must not point at this machine or a private network.");

        Exception? last = null;
        foreach (var address in addresses)
        {
            try
            {
                return await dial(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                last = ex;
            }
        }

        throw new HttpRequestException(HttpRequestError.ConnectionError, "No checked address accepted the connection.", last);
    }
}

/// <summary>
/// The transport. It keeps two connection pools: one that can hold only public addresses and
/// one for an instance that allows private networks. A change of the instance policy
/// therefore cannot reuse a connection that the new policy refuses. Redirects are not
/// followed, cookies are not kept and the machine's proxy settings are not used.
/// </summary>
public sealed class OutboundPrimaryHandler(PinnedConnector connector) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _publicOnly = Create(connector, publicOnly: true);
    private readonly HttpMessageInvoker _anyNetwork = Create(connector, publicOnly: false);

    private static HttpMessageInvoker Create(PinnedConnector connector, bool publicOnly) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = (context, ct) => connector.ConnectAsync(context, publicOnly, ct),
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        (OutboundCall.From(request).AllowPrivateNetwork ? _anyNetwork : _publicOnly).SendAsync(request, ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _publicOnly.Dispose();
            _anyNetwork.Dispose();
        }

        base.Dispose(disposing);
    }
}
