using System.Collections.Concurrent;

namespace SteamForge.Core.Proxy;

/// <summary>
/// Concurrent proxy pool with health-aware rotation. Rotation is index-based (not random)
/// so every proxy in the pool gets traffic; unhealthy entries go into cooldown and drop out
/// of rotation without being removed, so a temporary rate-limit does not permanently waste
/// a working proxy.
/// </summary>
public sealed class ProxyPool
{
    private readonly ConcurrentBag<ProxyEntry> _all = new();
    private int _cursor = -1;
    private int _failuresSinceSwap;

    public int Count => _all.Count;

    public void AddRange(IEnumerable<ProxyEntry> entries)
    {
        foreach (var e in entries) _all.Add(e);
    }

    /// <summary>
    /// Parses every proxy-line shape seen in the wild:
    ///   host:port
    ///   host:port:user:pass
    ///   user:pass@host:port
    ///   http://user:pass@host:port
    ///   socks5://host:port
    ///   socks4://host:port
    /// </summary>
    public static List<ProxyEntry> ParseLines(string content, string source = "file")
    {
        var result = new List<ProxyEntry>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var entry = Parse(line, source);
            if (entry is not null) result.Add(entry);
        }
        return result;
    }

    public static List<ProxyEntry> ParseFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new StreamReader(fs);
        return ParseLines(r.ReadToEnd(), Path.GetFileName(path));
    }

    private static ProxyEntry? Parse(string line, string source)
    {
        var scheme = ProxyScheme.Http;
        var rest = line;

        int schemeIdx = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var token = rest[..schemeIdx].ToLowerInvariant();
            scheme = token switch
            {
                "socks5" or "socks5h" => ProxyScheme.Socks5,
                "socks4" or "socks4a" => ProxyScheme.Socks4,
                "http" or "https" => ProxyScheme.Http,
                _ => ProxyScheme.Http,
            };
            rest = rest[(schemeIdx + 3)..];
        }

        string host, port, user = string.Empty, pass = string.Empty;

        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            var auth = rest[..at];
            var hostPort = rest[(at + 1)..];

            int authColon = auth.IndexOf(':');
            if (authColon >= 0)
            {
                user = auth[..authColon];
                pass = auth[(authColon + 1)..];
            }
            else user = auth;

            int hpColon = hostPort.IndexOf(':');
            if (hpColon < 0) return null;
            host = hostPort[..hpColon];
            port = hostPort[(hpColon + 1)..];
        }
        else
        {
            var parts = rest.Split(':');
            switch (parts.Length)
            {
                case 2:
                    host = parts[0];
                    port = parts[1];
                    break;
                case 4:
                    host = parts[0];
                    port = parts[1];
                    user = parts[2];
                    pass = parts[3];
                    break;
                case 5:
                    host = parts[0];
                    port = parts[1];
                    user = parts[2];
                    pass = parts[3] + ":" + parts[4];
                    break;
                default:
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(host)) return null;
        if (!int.TryParse(port.Trim(), out var portNum) || portNum is < 1 or > 65535) return null;

        return new ProxyEntry
        {
            Host = host.Trim(),
            Port = portNum,
            Scheme = scheme,
            Username = string.IsNullOrWhiteSpace(user) ? null : user,
            Password = string.IsNullOrEmpty(pass) ? null : pass,
            Source = source,
        };
    }

    /// <summary>
    /// Returns the next healthy proxy, or null when the pool is empty / exhausted.
    /// A proxy that just produced a verdict gets its consecutive-fail counter reset so a
    /// single rate-limit does not escalate into retirement.
    /// </summary>
    public ProxyEntry? Next()
    {
        var entries = _all.ToArray();
        if (entries.Length == 0) return null;

        for (int attempt = 0; attempt < entries.Length * 2; attempt++)
        {
            int idx = unchecked(Interlocked.Increment(ref _cursor)) % entries.Length;
            if (idx < 0) idx = -idx;
            var candidate = entries[idx];

            if (candidate.IsDead) continue;
            if (candidate.IsCoolingDown) continue;

            return candidate;
        }

        // Everything is cooling or dead — return the least-recently-punished live entry so
        // progress continues instead of stalling.
        return entries.FirstOrDefault(e => !e.IsDead);
    }

    public void MarkSuccess(ProxyEntry entry, long latencyMs)
        => entry.MarkSuccess(latencyMs);

    public void MarkFailure(ProxyEntry entry, TimeSpan cooldown)
    {
        entry.MarkFailure(cooldown);
        Interlocked.Increment(ref _failuresSinceSwap);
    }

    public void ResetCursor()
    {
        Interlocked.Exchange(ref _cursor, -1);
        Interlocked.Exchange(ref _failuresSinceSwap, 0);
    }

    public ProxySnapshot Snapshot()
    {
        var entries = _all.ToArray();
        int alive = entries.Count(e => !e.IsDead);
        int dead = entries.Length - alive;
        int cooling = entries.Count(e => !e.IsDead && e.IsCoolingDown);
        long totalLatency = 0;
        int latencySamples = 0;
        long totalS = 0, totalF = 0;

        foreach (var e in entries)
        {
            if (e.AvgLatencyMs >= 0) { totalLatency += e.AvgLatencyMs; latencySamples++; }
            totalS += e.Successes;
            totalF += e.Failures;
        }

        return new ProxySnapshot(
            entries.Length,
            alive,
            dead,
            cooling,
            latencySamples == 0 ? 0 : totalLatency / latencySamples,
            totalS + totalF == 0 ? 0 : (double)totalS / (totalS + totalF),
            entries.OrderByDescending(e => e.Successes).ToArray());
    }
}
