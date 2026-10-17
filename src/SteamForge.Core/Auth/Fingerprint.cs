namespace SteamForge.Core.Auth;

/// <summary>
/// Request fingerprint. Two things get a checker burned: a User-Agent that rotates every
/// request (real browsers do not do that — it reads as a bot) and Sec-* headers that do not
/// match the User-Agent's browser family. We ship a small pinned Chrome/Edge UA pool,
/// keep a UA sticky per proxy thread, and emit the matching client-hint headers.
/// </summary>
public sealed record Fingerprint(string UserAgent, string SecChUa, string SecChUaPlatform, bool IsMobile)
{
    // Chrome majors current as of the build. Bump these when Steam's edge gets noisy.
    private static readonly (string Chrome, string SecChUa)[] ChromePool =
    {
        ("131.0.6778.86", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\""),
        ("130.0.6723.70", "\"Google Chrome\";v=\"130\", \"Chromium\";v=\"130\", \"Not_A Brand\";v=\"24\""),
        ("129.0.6668.71", "\"Google Chrome\";v=\"129\", \"Chromium\";v=\"129\", \"Not_A Brand\";v=\"10\""),
        ("132.0.6834.83", "\"Google Chrome\";v=\"132\", \"Chromium\";v=\"132\", \"Not_A Brand\";v=\"24\""),
    };

    private static readonly (string Edge, string SecChUa)[] EdgePool =
    {
        ("131.0.2903.70", "\"Microsoft Edge\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\""),
        ("130.0.2849.56", "\"Microsoft Edge\";v=\"130\", \"Chromium\";v=\"130\", \"Not_A Brand\";v=\"24\""),
    };

    public static Fingerprint WindowsChrome()
    {
        var (v, sec) = ChromePool[System.Random.Shared.Next(ChromePool.Length)];
        return new Fingerprint(
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v} Safari/537.36",
            sec,
            "\"Windows\"",
            false);
    }

    public static Fingerprint WindowsEdge()
    {
        var (v, sec) = EdgePool[System.Random.Shared.Next(EdgePool.Length)];
        return new Fingerprint(
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v} Safari/537.36 Edg/{v}",
            sec,
            "\"Windows\"",
            false);
    }

    public static Fingerprint Random()
        => System.Random.Shared.Next(4) == 0 ? WindowsEdge() : WindowsChrome();

    /// <summary>Headers that must agree with the UA or the request reads synthetic.</summary>
    public IEnumerable<KeyValuePair<string, string>> ClientHints()
    {
        yield return new("sec-ch-ua", SecChUa);
        yield return new("sec-ch-ua-mobile", IsMobile ? "?1" : "?0");
        yield return new("sec-ch-ua-platform", SecChUaPlatform);
        yield return new("User-Agent", UserAgent);
    }
}
