using System.Diagnostics;

namespace SteamForge.Core.Proxy;

public enum ProxyScheme
{
    Http,
    Socks4,
    Socks5,
}

/// <summary>
/// A proxy plus its live health. Health is tracked per-proxy rather than per-request so a
/// pool can shed dead entries instead of paying the timeout cost on every retry.
/// </summary>
public sealed class ProxyEntry
{
    private long _success;
    private long _failures;
    private long _consecutiveFails;
    private long _lastLatencyMs = -1;
    private long _avgLatencyMs = -1;
    private long _cooldownUntilTicks;
    private long _deadMarked;

    public required string Host { get; init; }
    public required int Port { get; init; }
    public ProxyScheme Scheme { get; init; } = ProxyScheme.Http;
    public string? Username { get; init; }
    public string? Password { get; init; }

    public string Source { get; init; } = "manual";
    public long FirstSeenUtc { get; init; } = DateTime.UtcNow.Ticks;

    public long Successes => Interlocked.Read(ref _success);
    public long Failures => Interlocked.Read(ref _failures);
    public long ConsecutiveFails => Interlocked.Read(ref _consecutiveFails);
    public long AvgLatencyMs => Interlocked.Read(ref _avgLatencyMs);
    public long LastLatencyMs => Interlocked.Read(ref _lastLatencyMs);
    public bool IsDead => Interlocked.Read(ref _deadMarked) != 0;

    public bool IsCoolingDown => DateTime.UtcNow.Ticks < Interlocked.Read(ref _cooldownUntilTicks);

    public DateTime CooldownUntil => new(Interlocked.Read(ref _cooldownUntilTicks), DateTimeKind.Utc);

    public double SuccessRate
    {
        get
        {
            long s = Successes, f = Failures;
            long total = s + f;
            return total == 0 ? 0 : (double)s / total;
        }
    }

    public string SchemeToken => Scheme switch
    {
        ProxyScheme.Socks4 => "socks4",
        ProxyScheme.Socks5 => "socks5",
        _ => "http",
    };

    /// <summary>Uri form for HttpClient's IWebProxy / SocksProxy.</summary>
    public Uri ToUri()
    {
        var userInfo = string.IsNullOrEmpty(Username)
            ? string.Empty
            : $"{Uri.EscapeDataString(Username)}:{Uri.EscapeDataString(Password ?? string.Empty)}@";
        return new Uri($"{SchemeToken}://{userInfo}{Host}:{Port}");
    }

    public string Display
    {
        get
        {
            var auth = string.IsNullOrEmpty(Username) ? string.Empty : $"{Username}:***@";
            return $"{SchemeToken}://{auth}{Host}:{Port}";
        }
    }

    public void MarkSuccess(long latencyMs)
    {
        Interlocked.Increment(ref _success);
        Interlocked.Exchange(ref _consecutiveFails, 0);
        Interlocked.Exchange(ref _lastLatencyMs, latencyMs);

        long prev = Interlocked.Read(ref _avgLatencyMs);
        if (prev < 0) Interlocked.Exchange(ref _avgLatencyMs, latencyMs);
        else Interlocked.Exchange(ref _avgLatencyMs, (prev * 7 + latencyMs) / 8);
    }

    public void MarkFailure(TimeSpan cooldown)
    {
        Interlocked.Increment(ref _failures);
        long cf = Interlocked.Increment(ref _consecutiveFails);
        Interlocked.Exchange(ref _cooldownUntilTicks, DateTime.UtcNow.Add(cooldown).Ticks);

        // Persistently failing proxies are retired rather than left to eat timeouts forever.
        if (cf >= 4) Interlocked.Exchange(ref _deadMarked, 1);
    }

    public void Revive()
    {
        Interlocked.Exchange(ref _consecutiveFails, 0);
        Interlocked.Exchange(ref _deadMarked, 0);
    }
}

public sealed record ProxySnapshot(
    int Total,
    int Alive,
    int Dead,
    int CoolingDown,
    long AvgLatencyMs,
    double SuccessRate,
    IReadOnlyList<ProxyEntry> Entries);
