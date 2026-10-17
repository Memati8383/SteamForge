using System.Net;
using System.Text;
using SteamForge.Core.Auth;
using SteamForge.Core.Models;

namespace SteamForge.Probe;

/// <summary>
/// Fourth probe. Two questions, both answered from raw bytes rather than inferred:
///   1. What does Steam actually send back from GetPasswordRSAPublicKey? The parsed timestamp
///      came back empty, and an empty encryption_timestamp is a plausible cause of the 400.
///   2. What is in the 96-byte body Steam sends with the 400? Reading it needs a manual stream
///      read, because the connection closes before .NET's buffered read completes.
/// </summary>
internal static class Program4
{
    public static async Task Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        const string account = "mironov20011";

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        // ---- 1. raw RSA response ----
        var rsaUrl = "https://api.steampowered.com/IAuthenticationService/GetPasswordRSAPublicKey/v1"
                     + "?origin=" + Uri.EscapeDataString("https://store.steampowered.com")
                     + "&input_protobuf_encoded=" + Uri.EscapeDataString(AuthMessages.BuildRsaKeyRequest(account));

        Console.WriteLine("--- GetPasswordRSAPublicKey (raw) ---");
        var key = default(RsaKey);
        using (var req = new HttpRequestMessage(HttpMethod.Get, rsaUrl))
        {
            req.Version = System.Net.HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var raw = await ReadBodyTolerant(resp);
            Console.WriteLine($"HTTP {(int)resp.StatusCode}  {raw.Length} bytes");
            Console.WriteLine($"text: {Encoding.UTF8.GetString(raw)}");
            key = AuthMessages.ParseRsaKeyResponse(raw);
            if (key is not null)
                Console.WriteLine($"parsed: mod={key.ModulusHex.Length} exp={key.ExponentHex} ts='{key.Timestamp}'");
        }

        if (key is null || key.ModulusHex.Length == 0)
        {
            Console.WriteLine("no key — cannot continue");
            return;
        }

        var encrypted = SteamRsa.EncryptPassword("Wcwwgrhhehlach1", key.ModulusHex, key.ExponentHex);
        Console.WriteLine();
        Console.WriteLine($"encrypted password: {encrypted.Length} chars base64");
        Console.WriteLine();

        // ---- 2. BeginAuth, multipart ----
        Console.WriteLine("--- BeginAuth via multipart ---");
        var fp = Fingerprint.Random();
        await TryBegin(http, fp, BuildBegin(account, encrypted, key.Timestamp, fp), multipart: true);

        Console.WriteLine();
        Console.WriteLine("--- BeginAuth via application/x-www-form-urlencoded ---");
        await TryBegin(http, fp, BuildBegin(account, encrypted, key.Timestamp, fp), multipart: false);
    }

    private static (string Account, string Enc, string Ts, Fingerprint Fp) BuildBegin(
        string account, string encrypted, string ts, Fingerprint fp)
        => (account, encrypted, ts, fp);

    private static string BuildPayload(string account, string encrypted, string ts, Fingerprint fp)
        => AuthMessages.BuildBeginAuthRequest(
            account, encrypted, ts, AuthPlatformType.MobileApp, fp.UserAgent,
            rememberLogin: true, persistence: AuthPersistence.Persistent);

    private static async Task TryBegin(HttpClient http, Fingerprint fp,
        (string Account, string Enc, string Ts, Fingerprint Fp) args, bool multipart)
    {
        const string url = "https://api.steampowered.com/IAuthenticationService/BeginAuthSessionViaCredentials/v1";
        var payload = BuildPayload(args.Account, args.Enc, args.Ts, args.Fp);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (multipart)
        {
            var mp = new MultipartFormDataContent("----SteamForge" + Guid.NewGuid().ToString("N")[..12]);
            mp.Add(new StringContent(payload, Encoding.ASCII), "input_protobuf_encoded");
            req.Content = mp;
        }
        else
        {
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["input_protobuf_encoded"] = payload,
            });
        }

        req.Version = System.Net.HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        foreach (var kv in args.Fp.ClientHints())
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        req.Headers.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");

        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var body = await ReadBodyTolerant(resp);
            var xr = resp.Headers.TryGetValues("X-eresult", out var v) ? string.Join(",", v) : "<none>";
            Console.WriteLine($"HTTP {(int)resp.StatusCode}  X-eresult={xr}  body={body.Length} bytes");
            if (body.Length > 0)
                Console.WriteLine($"body: {Encoding.UTF8.GetString(body)}");
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Console.WriteLine($"FAILED {root.GetType().Name}: {root.Message}");
        }
    }

    /// <summary>
    /// Reads whatever bytes Steam actually sent. The buffered read throws when the connection
    /// closes short of the declared Content-Length, which hides the response that explains why.
    /// </summary>
    private static async Task<byte[]> ReadBodyTolerant(HttpResponseMessage resp)
    {
        try
        {
            return await resp.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            try
            {
                await using var s = await resp.Content.ReadAsStreamAsync();
                using var ms = new MemoryStream();
                var buf = new byte[4096];
                while (true)
                {
                    int n;
                    try { n = await s.ReadAsync(buf); }
                    catch { break; }
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
    }
}
