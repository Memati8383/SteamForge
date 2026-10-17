using System.Net;
using System.Text;
using SteamForge.Core.Auth;
using SteamForge.Core.Models;

namespace SteamForge.Probe;

/// <summary>
/// Second probe: exercises the checker's own code path (HttpFactory + Fingerprint + protobuf
/// URL) instead of a hand-rolled request. The first probe proved raw HTTP to Steam works, so
/// this narrows the failure to something the client itself adds.
/// </summary>
internal static class Program2
{
    public static async Task Run()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 1. Hand-rolled request, exact same URL shape the checker builds.
        const string account = "mironov20011";
        var encoded = AuthMessages.BuildRsaKeyRequest(account);
        var url = "https://api.steampowered.com/IAuthenticationService/GetPasswordRSAPublicKey/v1"
                  + "?origin=" + Uri.EscapeDataString("https://store.steampowered.com")
                  + "&input_protobuf_encoded=" + Uri.EscapeDataString(encoded);

        await Case("raw httpclient, no fp headers", async () =>
        {
            using var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Version = System.Net.HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            using var resp = await client.SendAsync(req);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Console.WriteLine($"       HTTP {(int)resp.StatusCode} bytes={bytes.Length}");
            return bytes.Length > 0;
        });

        // 2. Same, but with the checker's fingerprint headers applied.
        await Case("raw + Fingerprint.ClientHints headers", async () =>
        {
            var fp = Fingerprint.Random();
            using var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Version = System.Net.HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            foreach (var kv in fp.ClientHints())
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await client.SendAsync(req);
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Console.WriteLine($"       HTTP {(int)resp.StatusCode} bytes={bytes.Length}");
            return bytes.Length > 0;
        });

        // 3. The checker's own client, end to end.
        await Case("SteamWebAuthClient.GetRsaKeyAsync", async () =>
        {
            using var client = SteamWebAuthClient.Create(null, AuthMode.WebApiProtobuf, Fingerprint.Random());
            var key = await client.GetRsaKeyAsync(account);
            Console.WriteLine($"       modulus={key.ModulusHex.Length} hex chars, ts={key.Timestamp}");
            return key.ModulusHex.Length > 0;
        });

        // 4. What the checker sends: dump the fingerprint headers, to see if one is malformed.
        Console.WriteLine();
        Console.WriteLine("fingerprint headers in use:");
        foreach (var kv in Fingerprint.Random().ClientHints())
            Console.WriteLine($"  {kv.Key}: {kv.Value}");
    }

    private static async Task Case(string name, Func<Task<bool>> run)
    {
        Console.Write($"[{name,-42}] ");
        try
        {
            var ok = await run();
            Console.WriteLine(ok ? "OK" : "EMPTY");
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Console.WriteLine($"FAIL  {root.GetType().Name}: {root.Message}");
        }
    }
}
