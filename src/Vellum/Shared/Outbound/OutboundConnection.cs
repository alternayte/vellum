using System.Net;
using System.Net.Sockets;

namespace Vellum.Shared.Outbound;

/// <summary>Opens a TCP connection to one address. Tests replace it; the app uses a socket.</summary>
public delegate ValueTask<Stream> SocketDialer(IPEndPoint endpoint, CancellationToken ct);

/// <summary>Makes the transport under the delegating handlers. Tests replace it with a stub that serves fixtures.</summary>
public delegate HttpMessageHandler OutboundPrimaryHandlerFactory();

/// <summary>
/// Connects to the one address that the request was checked for and to no other. The
/// transport gives it that address as the host of the connection, so no name is resolved
/// here, and a name that changes its answer after the check (DNS rebinding) cannot move the
/// connection.
/// </summary>
public sealed class PinnedConnector(SocketDialer dial)
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
        var request = context.InitialRequestMessage;
        var call = OutboundCall.From(request);

        if (!IPAddress.TryParse(context.DnsEndPoint.Host, out var address)
            || !request.Options.TryGetValue(OutboundCall.PinnedAddressesKey, out var pinned)
            || !pinned.Contains(address))
            throw new OutboundException(OutboundFailure.UrlRefused, call, "The destination has no checked address.");

        // The check already refused these. This is the second lock on the same door.
        if (publicOnly && !OutboundUrlPolicy.IsPublic(address))
            throw new OutboundException(OutboundFailure.UrlRefused, call, "Must not point at this machine or a private network.");

        try
        {
            return await dial(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "The checked address did not accept the connection.", ex);
        }
    }
}

/// <summary>
/// The transport. Redirects are not followed, cookies are not kept and the machine's proxy
/// settings are not used.
/// <para>
/// A connection is kept open and used again, and the pool that keeps it is process-wide. Two
/// things decide which requests can share a connection. First, there are two pools: one that
/// can hold only public addresses and one for an instance that allows private networks, so a
/// change of the instance policy cannot reuse a connection that the new policy refuses.
/// Second, each request is sent to the address it was checked for, written as the host of the
/// request address, with the real name in the <c>Host</c> header (which also names the server
/// for TLS and for the certificate check). The pool therefore groups connections by address:
/// a request reuses a connection only when that connection goes to the address this request
/// was checked for, whatever name and workspace opened it.
/// </para>
/// <para>
/// A credential is a header of one request. It is never a property of a connection, so a
/// request on a shared connection carries its own credential or none.
/// </para>
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var call = OutboundCall.From(request);
        if (!request.Options.TryGetValue(OutboundCall.ChosenAddressKey, out var address) || request.RequestUri is not { } named)
            throw new OutboundException(OutboundFailure.UrlRefused, call, "The destination has no checked address.");

        var host = named.HostNameType == UriHostNameType.IPv6 ? named.Host : named.IdnHost;
        var hostHeader = request.Headers.Host;
        request.Headers.Host = named.IsDefaultPort ? host : $"{host}:{named.Port}";
        request.RequestUri = new UriBuilder(named) { Host = address.ToString() }.Uri;
        try
        {
            return await (call.AllowPrivateNetwork ? _anyNetwork : _publicOnly).SendAsync(request, ct);
        }
        finally
        {
            // Callers read the address of the request from the response. They see the name.
            request.RequestUri = named;
            request.Headers.Host = hostHeader;
        }
    }

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
