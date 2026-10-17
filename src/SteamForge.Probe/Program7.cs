using System.Net;
using System.Text;
using System.Text.Json;
using SteamForge.Core.Auth;
using SteamForge.Core.Models;
using SteamForge.Core.Proto;

namespace SteamForge.Probe;

/// <summary>
/// Isolates the encryption_timestamp wire type. JSON mode already proved the credential is good
/// (it returns client_id), so the remaining difference is how field 3 is encoded in the
/// protobuf request. Steam's own RSA response carries its timestamp as a varint, which suggests
/// the schema does not use fixed64 here — if so, a fixed64 field is misread and Steam rejects
/// with eresult 5 for every password, correct or not.
///
/// Each encoding is sent in turn and the answer is Steam's, not an assumption.
/// </summary>
internal static class Program7
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

        const string user = "mironov20011";
        const string pass = "Wcwwgrhhehlach1";

        var key = await FetchKeyAsync(http, user);
        if (key is null) { Console.WriteLine("no RSA key"); return; }
        Console.WriteLine($"user={user}  ts='{key.Timestamp}'  mod={key.ModulusHex.Length} hex");
        Console.WriteLine();

        var encrypted = SteamRsa.EncryptPassword(pass, key.ModulusHex, key.ExponentHex);
        var fp = Fingerprint.Random();
        ulong.TryParse(key.Timestamp, out var ts);

        var variants = new (string Label, Action<ProtobufWriter> Build)[]
        {
            ("field3 fixed64 (current)", w =>
            {
                w.WriteString(1, user).WriteString(2, encrypted)
                 .WriteFixed64(3, ts)
                 .WriteBool(4, true).WriteString(6, "Store")
                 .WriteEnum(7, (int)AuthPlatformType.MobileApp)
                 .WriteString(8, fp.UserAgent).WriteVarint(9, 1);
            }),
            ("field3 varint", w =>
            {
                w.WriteString(1, user).WriteString(2, encrypted)
                 .WriteVarint(3, ts)
                 .WriteBool(4, true).WriteString(6, "Store")
                 .WriteEnum(7, (int)AuthPlatformType.MobileApp)
                 .WriteString(8, fp.UserAgent).WriteVarint(9, 1);
            }),
            ("field3 string", w =>
            {
                w.WriteString(1, user).WriteString(2, encrypted)
                 .WriteString(3, key.Timestamp)
                 .WriteBool(4, true).WriteString(6, "Store")
                 .WriteEnum(7, (int)AuthPlatformType.MobileApp)
                 .WriteString(8, fp.UserAgent).WriteVarint(9, 1);
            }),
            ("field3 fixed64, ts=0", w =>
            {
                w.WriteString(1, user).WriteString(2, encrypted)
                 .WriteFixed64(3, 0UL)
                 .WriteBool(4, true).WriteString(6, "Store")
                 .WriteEnum(7, (int)AuthPlatformType.MobileApp)
                 .WriteString(8, fp.UserAgent).WriteVarint(9, 1);
            }),
            ("field3 omitted entirely", w =>
            {
                w.WriteString(1, user).WriteString(2, encrypted)
                 .WriteBool(4, true).WriteString(6, "Store")
                 .WriteEnum(7, (int)AuthPlatformType.MobileApp)
                 .WriteString(8, fp.UserAgent).WriteVarint(9, 1);
            }),
        };

        foreach (var (label, build) in variants)
        {
            var w = new ProtobufWriter();
            build(w);
            await SendAsync(http, fp, w.ToBase64(), label);
        }

        Console.WriteLine();
        Console.WriteLine("control — same protobuf shape, wrong password:");
        var badEnc = SteamRsa.EncryptPassword("definitely-wrong-xyz", key.ModulusHex, key.ExponentHex);
        var w2 = new ProtobufWriter();
        w2.WriteString(1, user).WriteString(2, badEnc)
           .WriteFixed64(3, ts)
           .WriteBool(4, true).WriteString(6, "Store")
           .WriteEnum(7, (int)AuthPlatformType.MobileApp)
           .WriteString(8, fp.UserAgent).WriteVarint(9, 1);
        await SendAsync(http, fp, w2.ToBase64(), "fixed64 + WRONG password");
    }

    private static async Task<RsaKey?> FetchKeyAsync(HttpClient http, string user)
    {
        var url = $"{RsaUrl}?origin={Uri.EscapeDataString("https://store.steampowered.com")}"
                  + $"&input_protobuf_encoded={Uri.EscapeDataString(AuthMessages.BuildRsaKeyRequest(user))}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Version = System.Net.HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        using var resp = await http.SendAsync(req);
        return AuthMessages.ParseRsaKeyResponse(await resp.Content.ReadAsByteArrayAsync());
    }

    private static async Task SendAsync(HttpClient http, Fingerprint fp, string payload, string label)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BeginUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["input_protobuf_encoded"] = payload,
            }),
        };
        req.Version = System.Net.HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        foreach (var kv in fp.ClientHints())
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        req.Headers.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");

        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var body = await ReadTolerant(resp);
            var xr = resp.Headers.TryGetValues("X-eresult", out var v) ? string.Join(",", v) : "-";

            string verdict;
            if (body.Length > 0 && System.Text.Encoding.UTF8.GetString(body).Contains("client_id"))
                verdict = "ACCEPTED (client_id present)";
            else if (xr == "1")
                verdict = "accepted-ish";
            else
                verdict = "rejected";

            Console.WriteLine($"  {label,-32} X-eresult={xr,-4} {verdict}");
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Console.WriteLine($"  {label,-32} FAILED {root.GetType().Name}: {root.Message}");
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
