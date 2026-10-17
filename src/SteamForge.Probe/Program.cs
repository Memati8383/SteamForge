using System.Net;
using System.Text;

namespace SteamForge.Probe;

/// <summary>
/// Minimal raw probe. Hits Steam's RSA endpoint with different HTTP version policies to isolate
/// a transport failure without any of the checker's own logic in the way.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        const string url = "https://api.steampowered.com/IAuthenticationService/GetPasswordRSAPublicKey/v1"
                           + "?origin=https%3A%2F%2Fstore.steampowered.com%26account_name=probeuser1234";

        var cases = new (string Name, Version Ver, HttpVersionPolicy Policy, DecompressionMethods Dec)[]
        {
            ("http/1.1 exact + gzip", HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, DecompressionMethods.GZip),
            ("http/1.1 exact + all", HttpVersion.Version11, HttpVersionPolicy.RequestVersionExact, DecompressionMethods.All),
            ("http/2 exact + gzip", new Version(2, 0), HttpVersionPolicy.RequestVersionExact, DecompressionMethods.GZip),
            ("default or-lower + gzip", HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower, DecompressionMethods.GZip),
            ("default or-lower, no decompress", HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower, DecompressionMethods.None),
        };

        foreach (var (name, ver, policy, dec) in cases)
        {            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = dec,
                UseCookies = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };

            try
            {
                using var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Version = ver;
                req.VersionPolicy = policy;
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");

                using var resp = await client.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                Console.WriteLine($"[OK]   {name,-32} HTTP {(int)resp.StatusCode} " +
                                  $"proto={resp.Version} len={body.Length}");
                Console.WriteLine($"       {Trunc(body, 160)}");
            }
            catch (Exception ex)
            {
                var root = ex;
                while (root.InnerException is not null) root = root.InnerException;
                Console.WriteLine($"[FAIL] {name,-32} {root.GetType().Name}: {root.Message}");
            }

            Console.WriteLine();
        }

        Console.WriteLine(new string('=', 78));
        await Program2.Run();
        Console.WriteLine(new string('=', 78));
        Program9.Run();
        return 0;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
