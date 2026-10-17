using System.Net;
using System.Net.Sockets;
using SteamForge.Core.Proxy;

namespace SteamForge.Core.Auth;

/// <summary>
/// Builds SocketsHttpHandler instances tuned for checker traffic.
///
/// Proxy handling is split because .NET only natively speaks HTTP proxies:
///   HTTP/HTTPS proxy → WebProxy, the handler tunnels with CONNECT itself.
///   SOCKS4/SOCKS5    → ConnectCallback, we perform the SOCKS handshake and return the socket.
/// </summary>
public static class HttpFactory
{
    public static SocketsHttpHandler Create(ProxyEntry? proxy, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(4),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = timeout,
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        };

        if (proxy is null)
        {
            handler.UseProxy = false;
            return handler;
        }

        if (proxy.Scheme == ProxyScheme.Http)
        {
            handler.UseProxy = true;
            handler.Proxy = BuildHttp(proxy);
            return handler;
        }

        // SOCKS: no proxy object — the tunnel is built per-connection instead.
        handler.UseProxy = false;
        handler.ConnectCallback = SocksConnector.Create(proxy);
        return handler;
    }

    public static HttpClient CreateClient(ProxyEntry? proxy, TimeSpan timeout, Fingerprint fp)
    {
        var handler = Create(proxy, timeout);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return client;
    }

    private static IWebProxy BuildHttp(ProxyEntry p)
    {
        var proxy = new WebProxy(p.Host, p.Port);
        if (!string.IsNullOrEmpty(p.Username))
            proxy.Credentials = new NetworkCredential(p.Username, p.Password ?? string.Empty);
        return proxy;
    }
}
