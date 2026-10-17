using System.Net;
using System.Text;
using SteamForge.Core.Auth;
using SteamForge.Core.Models;
using SteamForge.Core.Proto;
using SteamKit2.Internal;

namespace SteamForge.Probe;

/// <summary>
/// Compares my hand-rolled protobuf writer against SteamKit2's generated serializer, which is
/// the reference encoding of the very same message. Both payloads go to Steam, so the answer is
/// behavioural rather than a visual diff.
///
/// This matters because every field-3 variant I tried returned eresult 5 — including omitting
/// the field entirely. That points at something shared by all of them, i.e. the writer itself.
/// </summary>
internal static class Program8
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
        Console.WriteLine($"user={user} ts='{key.Timestamp}'");
        Console.WriteLine();

        var encrypted = SteamRsa.EncryptPassword(pass, key.ModulusHex, key.ExponentHex);
        var ua = Fingerprint.Random().UserAgent;
        ulong.TryParse(key.Timestamp, out var ts);

        // Reference encoding from SteamKit2's generated message class.
        var reference = new CAuthentication_BeginAuthSessionViaCredentials_Request
        {
            account_name = user,
            encrypted_password = encrypted,
            encryption_timestamp = ts,
            remember_login = true,
            website_id = "Store",
            platform_type = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
            device_friendly_name = ua,
            persistence = ESessionPersistence.k_ESessionPersistence_Persistent,
        };
        var referenceBytes = SerializeToBytes(reference);

        // My writer's encoding of the same logical request.
        var mine = new ProtobufWriter()
            .WriteString(1, user)
            .WriteString(2, encrypted)
            .WriteFixed64(3, ts)
            .WriteBool(4, true)
            .WriteString(6, "Store")
            .WriteEnum(7, (int)AuthPlatformType.MobileApp)
            .WriteString(8, ua)
            .WriteVarint(9, 1)
            .ToArray();

        Console.WriteLine($"steamkit2 payload : {referenceBytes.Length} bytes");
        Console.WriteLine($"my payload        : {mine.Length} bytes");
        Console.WriteLine($"identical         : {referenceBytes.AsSpan().SequenceEqual(mine)}");
        Console.WriteLine();

        Console.WriteLine("steamkit2 wire map:");
        Dump(referenceBytes);
        Console.WriteLine("my wire map:");
        Dump(mine);
        Console.WriteLine();

        var fp = Fingerprint.Random();
        await SendAsync(http, fp, Convert.ToBase64String(referenceBytes), "steamkit2 reference");
        await SendAsync(http, fp, Convert.ToBase64String(mine), "my writer");
    }

    private static byte[] SerializeToBytes(CAuthentication_BeginAuthSessionViaCredentials_Request msg)
    {
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, msg);
        return ms.ToArray();
    }

    private static void Dump(byte[] data)
    {
        var r = new ProtobufReader(data);
        ProtoValue? v;
        while ((v = r.Next()) is not null)
        {
            var val = v.Value.Kind switch
            {
                ProtoField.Varint => v.Value.Varint.ToString(),
                ProtoField.Fixed64 => v.Value.AsFixed64().ToString(),
                ProtoField.Fixed32 => BitConverter.ToUInt32(v.Value.Bytes, 0).ToString(),
                _ => v.Value.AsString().Length > 40
                        ? v.Value.AsString()[..40] + "…"
                        : v.Value.AsString(),
            };
            Console.WriteLine($"  field {v.Value.FieldNumber,-3} {v.Value.Kind,-8} {val}");
        }
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
            var text = body.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(body);
            var xr = resp.Headers.TryGetValues("X-eresult", out var v) ? string.Join(",", v) : "-";
            var verdict = text.Contains("client_id") ? "ACCEPTED" : "rejected";
            Console.WriteLine($"  {label,-20} HTTP {(int)resp.StatusCode} X-eresult={xr,-4} {verdict}");
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Console.WriteLine($"  {label,-20} FAILED {root.GetType().Name}: {root.Message}");
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
