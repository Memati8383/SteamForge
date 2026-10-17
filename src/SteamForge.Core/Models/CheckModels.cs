namespace SteamForge.Core.Models;

public enum CheckStatus
{
    /// <summary>Credentials accepted, no Steam Guard, access token obtained.</summary>
    Valid,

    /// <summary>Credentials accepted, Steam Guard required — token not obtainable.</summary>
    GuardLocked,

    /// <summary>Steam rejected the credentials definitively.</summary>
    Invalid,

    /// <summary>Account name does not resolve to any Steam account.</summary>
    NotFound,

    /// <summary>Rate limited on this IP/proxy. Not a credential verdict.</summary>
    RateLimited,

    /// <summary>Network, proxy, or parse failure. Not a credential verdict.</summary>
    Error,
}

public enum GuardKind
{
    None,
    EmailCode,
    DeviceCode,
    DeviceConfirmation,
}

public enum AuthMode
{
    /// <summary>IAuthenticationService over HTTP — fast, high concurrency.</summary>
    WebApiProtobuf,

    /// <summary>Same service, JSON transport (input_json).</summary>
    WebApiJson,

    /// <summary>SteamKit2 CM protocol — a real Steam client connection.</summary>
    NativeSteamKit,
}

public sealed class Combo
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? SourceLine { get; init; }
    public int LineNumber { get; init; }

    /// <summary>
    /// Metadata that trailed the credential on the source line (e.g. "GamesList = [...]").
    /// Kept separate so it is never submitted as part of the password.
    /// </summary>
    public string? Note { get; init; }

    public override string ToString() => $"{Username}:{Password}";
}

public sealed class AccountProfile
{
    public ulong SteamId { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string PersonaName { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "??";
    public string TimeZoneName { get; set; } = string.Empty;
    public int Level { get; set; } = -1;
    public int GameCount { get; set; } = -1;
    public List<OwnedGame> Games { get; } = new();
    public string WalletBalance { get; set; } = string.Empty;
    public string WalletCurrency { get; set; } = string.Empty;
    public string WalletCountry { get; set; } = string.Empty;
    public int VacBans { get; set; }
    public int GameBans { get; set; }
    public int DaysSinceLastBan { get; set; } = -1;
    public bool CommunityBanned { get; set; }
    public bool TradeBanned { get; set; }
    public bool LimitedAccount { get; set; }
    public DateTime AccountCreatedUtc { get; set; }
    public bool ProfilePublic { get; set; }
    /// <summary>True once Steam's profile XML actually parsed. Distinguishes "private" from "not fetched".</summary>
    public bool ProfileFetched { get; set; }
    public string AvatarFull { get; set; } = string.Empty;
}

public sealed class OwnedGame
{
    public int AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Lifetime playtime in minutes, straight from GetOwnedGames.</summary>
    public int PlaytimeForeverMinutes { get; init; }
    public bool HasCommunityVisibleStats { get; init; }
}

public sealed class CheckResult
{
    public required Combo Combo { get; init; }
    public required CheckStatus Status { get; init; }
    public GuardKind Guard { get; init; } = GuardKind.None;
    public AuthMode Mode { get; init; }
    public string? ProxyUsed { get; init; }
    public ulong SteamId { get; init; }
    public AccountProfile? Profile { get; init; }
    public string? FailureReason { get; init; }
    public int Attempts { get; init; } = 1;
    public TimeSpan Elapsed { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    public bool IsVerdict => Status is CheckStatus.Valid or CheckStatus.GuardLocked
        or CheckStatus.Invalid or CheckStatus.NotFound;

    public bool HasValue => Status == CheckStatus.Valid && Profile is not null;

    /// <summary>One-line human summary, used for the live log and hits.txt.</summary>
    public string SummaryLine()
    {
        var head = $"{Combo.Username}:{Combo.Password}";
        var p = Profile;
        return Status switch
        {
            CheckStatus.Valid when p is not null =>
                $"{head} | SteamID = {p.SteamId} | Level = {p.Level} | Games = {p.GameCount} | " +
                $"Country = {p.CountryCode} | Persona = {p.PersonaName} | " +
                $"Wallet = {p.WalletBalance} {p.WalletCurrency} | " +
                $"VAC = {p.VacBans} | GameBans = {p.GameBans} | TradeBan = {p.TradeBanned} | " +
                $"Limited = {p.LimitedAccount}",

            CheckStatus.Valid =>
                $"{head} | SteamID = {SteamId} | Guard = {Guard} | (enrichment skipped)",

            CheckStatus.GuardLocked =>
                $"{head} | SteamID = {SteamId} | Guard = {Guard} | token unavailable",

            CheckStatus.Invalid => $"{head} | invalid credentials",
            CheckStatus.NotFound => $"{head} | account not found",
            CheckStatus.RateLimited => $"{head} | rate limited ({FailureReason})",
            _ => $"{head} | error: {FailureReason}",
        };
    }
}
