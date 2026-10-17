using System.Net;
using System.Text;
using System.Text.Json;
using SteamForge.Core.Auth;
using SteamForge.Core.Models;

namespace SteamForge.Probe;

/// <summary>
/// Discriminating probe. The same credentials are sent over two independent serializations:
/// protobuf (input_protobuf_encoded) and JSON (input_json). If both answer eresult 5, the
/// credential itself is being rejected and the encoding is exonerated. If they disagree, the
/// protobuf path is still wrong.
///
/// A deliberately-wrong password runs alongside as a control, so a "5" from the real password
/// can be compared against a known-bad one rather than assumed meaningful.
/// </summary>
internal static class Program6
{
    private const string RsaUrl = "https://api.steampowered.com/IAuthenticationService/GetPasswordRSAPublicKey/v1";
    private const string BeginUrl = "https://api.steampowered.com/IAuthenticationService/BeginAuthSessionViaCredentials/v1";

    public static async Task Run()
    {
        Console.OutputEncoding = Encoding.UTF8;

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        var cases = new (string User, string Pass, string Label)[]
        {
            ("mironov20011", "Wcwwgrhhehlach1", "list credential"),
            ("mironov20011", "definitely-not-the-password-xyz", "CONTROL: known-bad"),
            ("razmik152", "Eoctoqkdflvsin1", "list credential"),
            ("froost9711", "Ygklceonkxjwit1", "list credential"),
        };

        foreach (var (user, pass, label) in cases)
        {
            Console.WriteLine($"=== {user}  [{label}] ===");
            var key = await FetchKeyAsync(http, user);
            if (key is null) { Console.WriteLine("  no RSA key\n"); continue; }
            Console.WriteLine($"  ts='{key.Timestamp}'");

            var encrypted = SteamRsa.EncryptPassword(pass, key.ModulusHex, key.ExponentHex);
            var fp = Fingerprint.Random();

            await ProbeProtobuf(http, fp, user, encrypted, key.Timestamp);
            await ProbeJson(http, fp, user, encrypted, key.Timestamp);
            Console.WriteLine();
        }
    }

    private static async Task<RsaKey?> FetchKeyAsync(HttpClient http, string user)
    {
        var url = $"{RsaUrl}?origin={Uri.EscapeDataString("https://store.steampowered.com")}"
                  + $"&input_protobuf_encoded={Uri.EscapeDataString(AuthMessages.BuildRsaKeyRequest(user))}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Version = System.Net.HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var raw = await ReadTolerant(resp);
            var key = AuthMessages.ParseRsaKeyResponse(raw);
            if (key is null)
                Console.WriteLine($"  RSA fetch: HTTP {(int)resp.StatusCode}, {raw.Length} bytes, unparsed");
            return key;
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Console.WriteLine($"  RSA fetch FAILED: {root.GetType().Name}: {root.Message}");
            return null;
        }
    }

    private static async Task ProbeProtobuf(HttpClient http, Fingerprint fp,
        string user, string encrypted, string ts)
    {
        var payload = AuthMessages.BuildBeginAuthRequest(
            user, encrypted, ts, AuthPlatformType.MobileApp, fp.UserAgent, rememberLogin: true, persistence: AuthPersistence.Persistent);

        using var req = new HttpRequestMessage(HttpMethod.Post, BeginUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["input_protobuf_encoded"] = payload,
            }),
        };
        ApplyHeaders(req, fp);
        await SendAndReport(http, req, "protobuf");
    }

    private static async Task ProbeJson(HttpClient http, Fingerprint fp,
        string user, string encrypted, string ts)
    {
        var json = JsonSerializer.Serialize(new
        {
            account_name = user,
            encrypted_password = encrypted,
            encryption_timestamp = ts,
            remember_login = true,
            website_id = "Store",
            platform_type = (int)AuthPlatformType.MobileApp,
            device_friendly_name = fp.UserAgent,
            persistence = 1,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, BeginUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["input_json"] = json }),
        };
        ApplyHeaders(req, fp);
        await SendAndReport(http, req, "json   ");
    }

    private static void ApplyHeaders(HttpRequestMessage req, Fingerprint fp)
    {
        req.Version = System.Net.HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        foreach (var kv in fp.ClientHints())
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        req.Headers.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");
    }

    private static async Task SendAndReport(HttpClient http, HttpRequestMessage req, string tag)
    {
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var body = await ReadTolerant(resp);
            var xr = resp.Headers.TryGetValues("X-eresult", out var v) ? string.Join(",", v) : "-";
            var bodyText = body.Length == 0 ? "<empty>" : Encoding.UTF8.GetString(body);
            if (bodyText.Length > 120) bodyText = bodyText[..120] + "…";
            Console.WriteLine($"  {tag}: HTTP {(int)resp.StatusCode}  X-eresult={xr,-6}  {bodyText}");
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Console.WriteLine($"  {tag}: FAILED {root.GetType().Name}: {root.Message}");
        }
    }

    private static async Task<byte[]> ReadTolerant(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsByteArrayAsync(); }
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
                    try { n = await s.ReadAsync(buf); } catch { break; }
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
            catch { return Array.Empty<byte>(); }
        }
    }
}
