using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Buffers.Binary;

namespace SteamForge.Core.Proxy;

/// <summary>
/// SOCKS4 / SOCKS5 CONNECT tunnel, exposed through SocketsHttpHandler.ConnectCallback.
///
/// .NET's HttpClient speaks HTTP proxies natively but has no SOCKS support at all — there is
/// no SocksProxy type in the BCL. Rather than drag in a proxy package, the handshake is done
/// by hand: open a TCP socket to the proxy, negotiate, ask it to open a stream to the real
/// Steam endpoint, and hand the raw socket back. The handler performs the TLS handshake on
/// top of whatever stream this callback returns, so we deliberately return plaintext.
/// </summary>
public static class SocksConnector
{
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create(ProxyEntry proxy)
    {
        return async (context, ct) =>
        {
            var target = context.DnsEndPoint;
            var client = new TcpClient { NoDelay = true };

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));

                await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token).ConfigureAwait(false);
                var stream = client.GetStream();

                bool ok = proxy.Scheme == ProxyScheme.Socks4
                    ? await HandshakeSocks4Async(stream, target, proxy, timeout.Token).ConfigureAwait(false)
                    : await HandshakeSocks5Async(stream, target, proxy, timeout.Token).ConfigureAwait(false);

                if (!ok)
                {
                    client.Dispose();
                    throw new IOException($"SOCKS handshake to {proxy.Host}:{proxy.Port} failed");
                }

                // Ownership of the socket transfers with the returned stream; the handler
                // wraps it in TLS for https requests.
                return new NetworkStream(client.Client, ownsSocket: true);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        };
    }

    // ---- SOCKS5 (RFC 1928 + RFC 1929 user/pass) ----

    private static async Task<bool> HandshakeSocks5Async(
        NetworkStream stream, DnsEndPoint target, ProxyEntry proxy, CancellationToken ct)
    {
        var hasAuth = !string.IsNullOrEmpty(proxy.Username);

        // Greeting: VER, NMETHODS, METHODS...
        var greet = hasAuth
            ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
            : new byte[] { 0x05, 0x01, 0x00 };
        await stream.WriteAsync(greet, ct).ConfigureAwait(false);

        var method = new byte[2];
        if (!await ReadExactAsync(stream, method, ct).ConfigureAwait(false)) return false;
        if (method[0] != 0x05) return false;

        if (method[1] == 0x02)
        {
            if (!hasAuth) return false;
            if (!await Socks5UserPassAsync(stream, proxy, ct).ConfigureAwait(false)) return false;
        }
        else if (method[1] != 0x00)
        {
            return false;
        }

        // CONNECT request.
        var hostBytes = System.Text.Encoding.ASCII.GetBytes(target.Host);
        var request = new byte[7 + hostBytes.Length];
        request[0] = 0x05; // VER
        request[1] = 0x01; // CMD = CONNECT
        request[2] = 0x00; // RSV
        request[3] = 0x03; // ATYP = DOMAINNAME
        request[4] = (byte)hostBytes.Length;
        Buffer.BlockCopy(hostBytes, 0, request, 5, hostBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(5 + hostBytes.Length), (ushort)target.Port);

        await stream.WriteAsync(request, ct).ConfigureAwait(false);

        // Reply: VER, REP, RSV, ATYP, [ADDR], PORT
        var head = new byte[4];
        if (!await ReadExactAsync(stream, head, ct).ConfigureAwait(false)) return false;
        if (head[0] != 0x05 || head[1] != 0x00) return false;

        int addrLen = head[3] switch
        {
            0x01 => 4,      // IPv4
            0x03 => -1,     // domain: length-prefixed
            0x04 => 16,     // IPv6
            _ => -2,
        };

        if (addrLen == -2) return false;
        if (addrLen == -1)
        {
            var lenBuf = new byte[1];
            if (!await ReadExactAsync(stream, lenBuf, ct).ConfigureAwait(false)) return false;
            addrLen = lenBuf[0];
        }

        var skip = new byte[addrLen + 2];
        return await ReadExactAsync(stream, skip, ct).ConfigureAwait(false);
    }

    private static async Task<bool> Socks5UserPassAsync(NetworkStream stream, ProxyEntry proxy, CancellationToken ct)
    {
        var user = System.Text.Encoding.UTF8.GetBytes(proxy.Username ?? string.Empty);
        var pass = System.Text.Encoding.UTF8.GetBytes(proxy.Password ?? string.Empty);
        if (user.Length > 255 || pass.Length > 255) return false;

        var msg = new byte[3 + user.Length + pass.Length];
        msg[0] = 0x01; // RFC 1929 sub-negotiation version
        msg[1] = (byte)user.Length;
        Buffer.BlockCopy(user, 0, msg, 2, user.Length);
        msg[2 + user.Length] = (byte)pass.Length;
        Buffer.BlockCopy(pass, 0, msg, 3 + user.Length, pass.Length);

        await stream.WriteAsync(msg, ct).ConfigureAwait(false);

        var reply = new byte[2];
        if (!await ReadExactAsync(stream, reply, ct).ConfigureAwait(false)) return false;
        return reply[1] == 0x00;
    }

    // ---- SOCKS4 / SOCKS4a ----

    private static async Task<bool> HandshakeSocks4Async(
        NetworkStream stream, DnsEndPoint target, ProxyEntry proxy, CancellationToken ct)
    {
        var user = System.Text.Encoding.ASCII.GetBytes(proxy.Username ?? "steamforge");

        byte[] ip;
        byte[]? domainSuffix = null;

        if (IPAddress.TryParse(target.Host, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            ip = parsed.GetAddressBytes();
        }
        else
        {
            // SOCKS4a: a dummy non-routable IP (0.0.0.x is the documented marker) followed by
            // the domain, NUL-terminated, so the proxy resolves the name remotely.
            ip = new byte[] { 0, 0, 0, 1 };
            domainSuffix = System.Text.Encoding.ASCII.GetBytes(target.Host + "\0");
        }

        int len = 8 + user.Length + 1 + (domainSuffix?.Length ?? 0);
        var request = new byte[len];
        request[0] = 0x04;
        request[1] = 0x01; // CONNECT
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)target.Port);
        Buffer.BlockCopy(ip, 0, request, 4, 4);
        Buffer.BlockCopy(user, 0, request, 8, user.Length);
        // position 8+user.Length is the NUL terminator of the userid
        if (domainSuffix is not null)
            Buffer.BlockCopy(domainSuffix, 0, request, 9 + user.Length, domainSuffix.Length);

        await stream.WriteAsync(request, ct).ConfigureAwait(false);

        var reply = new byte[8];
        if (!await ReadExactAsync(stream, reply, ct).ConfigureAwait(false)) return false;
        return reply[0] == 0x00 && reply[1] == 0x5A;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
