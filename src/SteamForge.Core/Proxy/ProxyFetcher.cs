using System.Collections.Concurrent;

namespace SteamForge.Core.Proxy;

public sealed record ProxyFetchResult(
    IReadOnlyList<ProxyEntry> Proxies,
    IReadOnlyList<(string Source, int Count, string? Error)> Report);

/// <summary>
/// Pulls free proxies from public list endpoints, then optionally self-filters by testing
/// each candidate against the exact Steam endpoint the checker will use. Free lists are
/// ~90% dead on arrival, so fetching and testing are separate steps: fetch wide, test narrow,
/// keep only what actually answered Steam.
/// </summary>
public static class ProxyFetcher
{
    private static readonly (string Url, ProxyScheme Scheme)[] Sources =
    {
        ("https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt", ProxyScheme.Http),
        ("https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/socks5.txt", ProxyScheme.Socks5),
        ("https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt", ProxyScheme.Http),
        ("https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/socks5.txt", ProxyScheme.Socks5),
        ("https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/protocols/http/data.txt", ProxyScheme.Http),
        ("https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/protocols/socks5/data.txt", ProxyScheme.Socks5),
        ("https://raw.githubusercontent.com/roosterkid/openproxylist/main/HTTPS_RAW.txt", ProxyScheme.Http),
        ("https://raw.githubusercontent.com/officialputuid/Kangproxy/Kangproxy/http.txt", ProxyScheme.Http),
        ("https://raw.githubusercontent.com/officialputuid/Kangproxy/Kangproxy/socks5.txt", ProxyScheme.Socks5),
    };

    public static async Task<ProxyFetchResult> FetchAsync(
        bool includeHttp = true,
        bool includeSocks5 = true,
        bool includeSocks4 = false,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        var merged = new ConcurrentDictionary<string, ProxyEntry>(StringComparer.OrdinalIgnoreCase);
        var report = new List<(string, int, string?)>();

        var tasks = Sources
            .Where(s => (s.Scheme == ProxyScheme.Http && includeHttp)
                     || (s.Scheme == ProxyScheme.Socks5 && includeSocks5)
                     || (s.Scheme == ProxyScheme.Socks4 && includeSocks4))
            .Select(async s =>
            {
                try
                {
                    var body = await http.GetStringAsync(s.Url, ct).ConfigureAwait(false);
                    var parsed = ProxyPool.ParseLines(body, s.Url.Split('/')[^1]);
                    foreach (var e in parsed)
                    {
                        if (e.Scheme == s.Scheme)
                            merged.TryAdd($"{e.SchemeToken}://{e.Host}:{e.Port}", e);
                    }
                    lock (report) report.Add((s.Url, parsed.Count, null));
                }
                catch (Exception ex)
                {
                    lock (report) report.Add((s.Url, 0, ex.Message));
                }
            });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var list = merged.Values.ToList();
        Shuffle(list);
        return new ProxyFetchResult(list, report);
    }

    public static void Shuffle(List<ProxyEntry> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
