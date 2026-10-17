using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SteamForge.Core.Models;

namespace SteamForge.Core.Auth;

/// <summary>
/// Pulls the profile detail behind a valid login.
///
/// Nothing here needs a Steam Web API key. The Web API's GetPlayerSummaries/GetPlayerBans
/// reject an OAuth access token in the `key=` slot (HTTP 403 "verify your key parameter"),
/// so those two calls are only attempted when a key is supplied; the SteamID-only XML
/// profile endpoint carries the same persona/visibility/ban state without one.
///
/// Two enrichment paths exist because a guard-locked account has no access token:
///   EnrichAsync        — full profile, needs an access_token for level/games/wallet.
///   EnrichPublicAsync  — SteamID-only, XML profile, works with no token at all.
/// Guard-locked accounts are not thrown away; they still get scored.
/// </summary>
public sealed class SteamApi
{
    private static readonly HttpClient Shared = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(3),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly HttpClient _http;
    private readonly string? _webApiKey;

    // Steam answers a burst with a throttled or empty response and recovers in seconds;
    // one pass over the list is not enough to ride that out.
    private static readonly int[] RetryDelaysMs = { 500, 1500, 4000 };
    private static readonly int[] CommunityRetryDelaysMs = { 3000, 9000, 25000 };

    // Measured against live Steam:6 profile XML requests at 2s spacing all return200,
    // back-to-back requests trip a429 that outlives a normal retry window. The gate is
    // process-wide on purpose — every worker shares one IP and one allowance.
    private static readonly TimeSpan CommunityMinInterval = TimeSpan.FromMilliseconds(1800);
    private static readonly object CommunityGateSync = new();
    private static long _communityLastTicks;

    private static readonly TimeSpan SubFetchStagger = TimeSpan.FromMilliseconds(180);

    public SteamApi(HttpClient? http = null, string? webApiKey = null)
    {
        _http = http ?? Shared;
        _webApiKey = string.IsNullOrWhiteSpace(webApiKey) ? null : webApiKey;
    }

    public async Task<AccountProfile> EnrichAsync(string accessToken, ulong steamId, CancellationToken ct = default)
    {
        var profile = new AccountProfile { SteamId = steamId, AccessToken = accessToken };

        var tasks = new List<Task>
        {
            StaggeredAsync(0, ct, () => FillProfileXmlAsync(profile, steamId, ct)),
            StaggeredAsync(1, ct, () => FillLevelAsync(profile, accessToken, steamId, ct)),
            StaggeredAsync(2, ct, () => FillGamesAsync(profile, accessToken, steamId, ct)),
            StaggeredAsync(3, ct, () => FillAccountPageAsync(profile, accessToken, steamId, ct)),
        };

        if (_webApiKey is not null)
        {
            tasks.Add(StaggeredAsync(4, ct, () => FillSummariesAsync(profile, steamId, ct)));
            tasks.Add(StaggeredAsync(5, ct, () => FillBansAsync(profile, steamId, ct)));
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* each filler swallows its own failures */ }

        return profile;
    }

    /// <summary>Offsetting each sub-fetch keeps parallel requests from landing as one burst.</summary>
    private static async Task StaggeredAsync(int slot, CancellationToken ct, Func<Task> fetch)
    {
        if (slot > 0)
        {
            var delay = TimeSpan.FromTicks(SubFetchStagger.Ticks * slot);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        await fetch().ConfigureAwait(false);
    }

    public async Task<AccountProfile> EnrichPublicAsync(ulong steamId, CancellationToken ct = default)
    {
        var profile = new AccountProfile { SteamId = steamId };
        await FillProfileXmlAsync(profile, steamId, ct).ConfigureAwait(false);

        if (_webApiKey is not null)
            await FillSummariesAsync(profile, steamId, ct).ConfigureAwait(false);

        return profile;
    }

    // ---- SteamID-only profile XML: persona, visibility, VAC, trade, limited ----

    private async Task FillProfileXmlAsync(AccountProfile p, ulong steamId, CancellationToken ct)
    {
        var text = await GetCommunityTextAsync($"https://steamcommunity.com/profiles/{steamId}?xml=1", ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(text)) return;

        XElement? root;
        try
        {
            root = XDocument.Parse(text).Root;
        }
        catch
        {
            return; // Steam serves an HTML error page here for unknown IDs.
        }

        if (root is null || root.Name.LocalName != "profile") return;

        p.ProfileFetched = true;
        p.PersonaName = Node(root, "steamID");
        p.AvatarFull = Node(root, "avatarFull");

        p.ProfilePublic = string.Equals(Node(root, "privacyState"), "public",
            StringComparison.OrdinalIgnoreCase);

        var created = Node(root, "memberSince");
        if (created.Length > 0 &&
            DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var since))
        {
            p.AccountCreatedUtc = since;
        }

        // XML reports these as flags, so VacBans is a 0/1 here; a real ban count needs an API key.
        p.VacBans = Node(root, "vacBanned") == "1" ? 1 : 0;

        var trade = Node(root, "tradeBanState");
        p.TradeBanned = trade.Length > 0 && !string.Equals(trade, "None", StringComparison.OrdinalIgnoreCase);

        p.LimitedAccount = Node(root, "isLimitedAccount") == "1";

        // A private profile omits <steamID>, so the page title is the persona source of record.
        if (string.IsNullOrEmpty(p.PersonaName))
            await FillProfileTitleAsync(p, steamId, ct).ConfigureAwait(false);
    }

    /// <summary>Last-resort persona source: the profile page's &lt;title&gt;, readable even when private.</summary>
    private async Task FillProfileTitleAsync(AccountProfile p, ulong steamId, CancellationToken ct)
    {
        var html = await GetCommunityTextAsync($"https://steamcommunity.com/profiles/{steamId}", ct)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return;

        var m = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return;

        var title = m.Groups[1].Value.Trim();
        const string marker = "Steam Community :: ";
        var at = title.IndexOf(marker, StringComparison.Ordinal);
        p.PersonaName = at >= 0 ? title[(at + marker.Length)..].Trim() : title;
    }

    // ---- GetPlayerSummaries (only with a Web API key) ----

    private async Task FillSummariesAsync(AccountProfile p, ulong steamId, CancellationToken ct)
    {
        var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/" +
                  $"?key={_webApiKey}&steamids={steamId}";
        var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (doc is null) return;

        var root = doc.Value;
        if (!root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object) return;
        if (!resp.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return;

        foreach (var pl in players.EnumerateArray())
        {
            if (!pl.TryGetProperty("steamid", out var sid) || sid.GetString() != steamId.ToString()) continue;

            if (string.IsNullOrEmpty(p.PersonaName)) p.PersonaName = Str(pl, "personaname");

            var cc = Str(pl, "loccountrycode");
            if (cc.Length > 0) p.CountryCode = cc;

            p.TimeZoneName = Str(pl, "locstatecode");
            if (string.IsNullOrEmpty(p.AvatarFull)) p.AvatarFull = Str(pl, "avatarfull");
            p.ProfilePublic = Str(pl, "communityvisibilitystate") == "3";

            if (pl.TryGetProperty("timecreated", out var created) && created.ValueKind == JsonValueKind.Number)
                p.AccountCreatedUtc = DateTimeOffset.FromUnixTimeSeconds(created.GetInt64()).UtcDateTime;

            break;
        }
    }

    // ---- GetPlayerBans (only with a Web API key) ----

    private async Task FillBansAsync(AccountProfile p, ulong steamId, CancellationToken ct)
    {
        var doc = await GetJsonAsync(
            $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={_webApiKey}&steamids={steamId}",
            ct).ConfigureAwait(false);
        if (doc is null) return;

        var root = doc.Value;
        if (!root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) return;

        foreach (var pl in players.EnumerateArray())
        {
            if (!pl.TryGetProperty("SteamId", out var sid) || sid.GetString() != steamId.ToString()) continue;

            p.VacBans = Int(pl, "NumberOfVACBans");
            p.GameBans = Int(pl, "NumberOfGameBans");
            p.CommunityBanned = Bool(pl, "CommunityBanned");

            var economy = Str(pl, "EconomyBan");
            p.TradeBanned = !string.IsNullOrEmpty(economy) && economy != "none";

            p.LimitedAccount = Bool(pl, "AccountLimited");
            p.DaysSinceLastBan = Int(pl, "DaysSinceLastBan");
            break;
        }
    }

    // ---- GetSteamLevel ----

    private async Task FillLevelAsync(AccountProfile p, string token, ulong steamId, CancellationToken ct)
    {
        var doc = await GetJsonAsync(
            $"https://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?access_token={token}&steamid={steamId}",
            ct).ConfigureAwait(false);
        if (doc is null) return;

        var root = doc.Value;
        if (root.TryGetProperty("response", out var resp) &&
            resp.TryGetProperty("player_level", out var lvl) &&
            lvl.ValueKind == JsonValueKind.Number)
        {
            p.Level = lvl.GetInt32();
        }
    }

    // ---- GetOwnedGames ----

    private async Task FillGamesAsync(AccountProfile p, string token, ulong steamId, CancellationToken ct)
    {
        var doc = await GetJsonAsync(
            $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?access_token={token}" +
            $"&steamid={steamId}&include_appinfo=1&include_played_free_games=1", ct).ConfigureAwait(false);
        if (doc is null) return;

        var root = doc.Value;
        if (!root.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object) return;

        if (resp.TryGetProperty("game_count", out var gc) && gc.ValueKind == JsonValueKind.Number)
            p.GameCount = gc.GetInt32();

        if (!resp.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array) return;

        foreach (var g in games.EnumerateArray())
        {
            var appid = Int(g, "appid");
            if (appid <= 0) continue;

            var name = Str(g, "name");
            p.Games.Add(new OwnedGame
            {
                AppId = appid,
                Name = string.IsNullOrEmpty(name) ? NotableGames.Name(appid) : name,
                PlaytimeForeverMinutes = Int(g, "playtime_forever"),
                HasCommunityVisibleStats = Bool(g, "has_community_visible_stats"),
            });
        }

        p.Games.Sort((a, b) => b.PlaytimeForeverMinutes.CompareTo(a.PlaytimeForeverMinutes));
        if (p.GameCount < 0) p.GameCount = p.Games.Count;
    }

    // ---- Wallet balance and store country (account page scrape with a forged session cookie) ----

    private static readonly Regex WalletBalanceRe = new(
        @"id=""header_wallet_balance""[^>]*>\s*([^<]+)", RegexOptions.Compiled);

    private static readonly Regex AccountPriceRe = new(
        @"class=""accountData price""[^>]*>\s*([^<]+)", RegexOptions.Compiled);

    private static readonly Regex AccountCountryRe = new(
        @"class=""account_data_field""[^>]*>\s*([^<]+)", RegexOptions.Compiled);

    private async Task FillAccountPageAsync(AccountProfile p, string accessToken, ulong steamId, CancellationToken ct)
    {
        try
        {
            // The account page reads the session straight from the steamLoginSecure cookie,
            // which for the web layer is literally "{steamid}||{access_token}".
            var forged = $"{steamId}||{accessToken}";
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://store.steampowered.com/account/");
            req.Headers.TryAddWithoutValidation("Cookie", $"steamLoginSecure={Uri.EscapeDataString(forged)}");
            req.Headers.TryAddWithoutValidation("User-Agent", Fingerprint.Random().UserAgent);
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Family View locks the account page; balance is unknowable, so leave it empty
            // rather than reporting a scrape artifact as a real balance.
            if (html.Contains("Family View", StringComparison.OrdinalIgnoreCase)) return;

            var m = WalletBalanceRe.Match(html);
            if (!m.Success) m = AccountPriceRe.Match(html);
            if (m.Success) p.WalletBalance = m.Groups[1].Value.Trim();

            var c = AccountCountryRe.Match(html);
            if (!c.Success) return;

            // Steam prints the full country name here, not the ISO code.
            var country = c.Groups[1].Value.Trim();
            p.WalletCountry = country;
            if (p.CountryCode == "??") p.CountryCode = country;
        }
        catch
        {
            // Wallet is a nice-to-have; never fail a hit over it.
        }
    }

    // ---- helpers ----

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            int status;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", Fingerprint.Random().UserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                status = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(bytes);
                    return doc.RootElement.Clone();
                }

                // A 403 here is Steam refusing the token in `key=`, not a transient failure.
                if (!IsTransient(status)) return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = -1;
                if (Environment.GetEnvironmentVariable("SF_DIAG") == "1")
                    Console.WriteLine($"    [diag] EXC {ex.GetType().Name}: {ex.Message} {url}");
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (Environment.GetEnvironmentVariable("SF_DIAG") == "1")
                Console.WriteLine($"    [diag] {status} attempt={attempt} {url}");

            if (attempt >= RetryDelaysMs.Length) return null;
            try { await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
    }

    /// <summary>
    /// steamcommunity.com answers a burst of profile requests with429 and then holds the
    /// block for minutes, so community traffic goes through a shared pacing gate and backs
    /// off far longer than an ordinary retry.
    /// </summary>
    private async Task<string> GetCommunityTextAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            int status;
            try
            {
                await CommunityGateAsync(ct).ConfigureAwait(false);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", Fingerprint.Random().UserAgent);
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                status = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!IsTransient(status)) return string.Empty;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = -1;
                if (Environment.GetEnvironmentVariable("SF_DIAG") == "1")
                    Console.WriteLine($"    [diag] EXC {ex.GetType().Name}: {ex.Message} {url}");
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }

            if (Environment.GetEnvironmentVariable("SF_DIAG") == "1")
                Console.WriteLine($"    [diag] {status} attempt={attempt} {url}");

            if (attempt >= RetryDelaysMs.Length) return string.Empty;

            var delay = CommunityRetryDelaysMs[attempt];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return string.Empty; }
        }
    }

    private static async Task CommunityGateAsync(CancellationToken ct)
    {
        while (true)
        {
            long waitMs;
            lock (CommunityGateSync)
            {
                var now = DateTime.UtcNow.Ticks;
                var since = now - _communityLastTicks;
                if (since >= CommunityMinInterval.Ticks)
                {
                    _communityLastTicks = now;
                    return;
                }

                waitMs = (CommunityMinInterval.Ticks - since) / TimeSpan.TicksPerMillisecond + 1;
            }

            await Task.Delay((int)waitMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Throttling and transient upstream noise; anything else is a real answer.</summary>
    private static bool IsTransient(int status)
        => status is 408 or 429 or 500 or 502 or 503 or 504 || status == -1;

    private static string Node(XElement root, string name)
        => root.Element(name)?.Value.Trim() ?? string.Empty;

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static int Int(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool Bool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) &&
           (v.ValueKind == JsonValueKind.True ||
            (v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0));
}
