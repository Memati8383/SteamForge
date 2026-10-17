using System.Net;
using System.Text;
using SteamForge.Core.Auth;
using SteamForge.Core.Models;

namespace SteamForge.Probe;

/// <summary>
/// Third probe: replays the exact BeginAuth POST the checker sends, but reads the response
/// manually and prints its headers before touching the body. The first two probes proved the
/// RSA GET works, so the failure is specific to this POST — this separates "SendAsync threw"
/// from "reading the body threw", and shows what Steam actually sent back.
/// </summary>
internal static class Program3
{
    public static async Task Run()
    {
        Console.OutputEncoding = Encoding.UTF8;

        const string account = "mironov20011";

        using var client = SteamWebAuthClient.Create(null, AuthMode.WebApiProtobuf, Fingerprint.Random());
        var key = await client.GetRsaKeyAsync(account);
        var encrypted = SteamRsa.EncryptPassword("Wcwwgrhhehlach1", key.ModulusHex, key.ExponentHex);

        var fp = Fingerprint.Random();
        var encoded = AuthMessages.BuildBeginAuthRequest(
            account, encrypted, key.Timestamp,
            AuthPlatformType.MobileApp, fp.UserAgent, rememberLogin: true, persistence: AuthPersistence.Persistent);

        var boundary = "----SteamForge" + Guid.NewGuid().ToString("N")[..12];
        using var multipart = new MultipartFormDataContent(boundary);
        multipart.Add(new StringContent(encoded, Encoding.ASCII), "input_protobuf_encoded");

        Console.WriteLine($"RSA ts='{key.Timestamp}'  proto payload {encoded.Length} chars");
        Console.WriteLine();

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        // Endpoints is internal to Core, so the probe spells the URL out.
        const string beginAuthUrl = "https://api.steampowered.com/IAuthenticationService/BeginAuthSessionViaCredentials/v1";
        using var req = new HttpRequestMessage(HttpMethod.Post, beginAuthUrl) { Content = multipart };
        req.Version = System.Net.HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        foreach (var kv in fp.ClientHints())
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        req.Headers.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            Console.WriteLine($"SendAsync OK  HTTP {(int)resp.StatusCode} proto={resp.Version}");
        }
        catch (Exception ex)
        {
            var root = Root(ex);
            Console.WriteLine($"SendAsync FAILED  {root.GetType().Name}: {root.Message}");
            return;
        }

        using (resp)
        {
            Console.WriteLine("response headers:");
            foreach (var h in resp.Headers)
                Console.WriteLine($"  {h.Key}: {string.Join(", ", h.Value)}");
            if (resp.Content.Headers.ContentLength is { } cl)
                Console.WriteLine($"  Content-Length: {cl}");
            Console.WriteLine($"  Content-Type: {resp.Content.Headers.ContentType}");
            Console.WriteLine();

            try
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                Console.WriteLine($"body read OK  {bytes.Length} bytes");
                Console.WriteLine($"X-eresult: {(resp.Headers.TryGetValues("X-eresult", out var v) ? string.Join(",", v) : "<none>")}");
                if (bytes.Length > 0)
                {
                    Console.WriteLine($"first bytes: {BitConverter.ToString(bytes.AsSpan(0, Math.Min(24, bytes.Length)).ToArray())}");
                    Console.WriteLine($"as text: {Trunc(Encoding.UTF8.GetString(bytes), 200)}");
                }
            }
            catch (Exception ex)
            {
                var root = Root(ex);
                Console.WriteLine($"body read FAILED  {root.GetType().Name}: {root.Message}");
            }
        }
    }

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
