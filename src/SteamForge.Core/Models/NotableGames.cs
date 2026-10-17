namespace SteamForge.Core.Models;

/// <summary>
/// AppIDs whose presence materially changes an account's worth. Used for the value filter
/// and the "notable games" highlight. Extend freely — this list is a scoring aid, not a
/// game database.
/// </summary>
public static class NotableGames
{
    private static readonly Dictionary<int, string> ByAppId = new()
    {
        [730] = "Counter-Strike 2",
        [570] = "Dota 2",
        [440] = "Team Fortress 2",
        [578080] = "PUBG: BATTLEGROUNDS",
        [252490] = "Rust",
        [1172470] = "Apex Legends",
        [1245620] = "Elden Ring",
        [1091500] = "Cyberpunk 2077",
        [292030] = "The Witcher 3: Wild Hunt",
        [1938090] = "Call of Duty",
        [359550] = "Rainbow Six Siege",
        [381210] = "Dead by Daylight",
        [311210] = "Call of Duty: Black Ops III",
        [218620] = "PAYDAY 2",
        [346110] = "ARK: Survival Evolved",
        [413150] = "Stardew Valley",
        [49520] = "Borderlands 2",
        [105600] = "Terraria",
        [620] = "Portal 2",
        [271590] = "GTA V",
        [236390] = "War Thunder",
        [550] = "Left 4 Dead 2",
        [10] = "Counter-Strike: Source",
        [240] = "Counter-Strike: Condition Zero",
        [400] = "Portal",
        [443220] = "Tabletop Simulator",
        [1145360] = "Hades",
        [1817070] = "Marvel's Spider-Man Remastered",
        [1888930] = "The Last of Us Part I",
        [2050650] = "Resident Evil 4",
        [1903340] = "Lethal Company",
        [1623730] = "Palworld",
        [2358720] = "Black Myth: Wukong",
        [1085660] = "Destiny 2",
        [1238810] = "Baldur's Gate 3",
        [359551] = "Rainbow Six Extraction",
        [990080] = "Hogwarts Legacy",
        [2767030] = "Marvel Rivals",
        [3164500] = "Schedule I",
    };

    public static bool IsNotable(int appId) => ByAppId.ContainsKey(appId);

    public static string Name(int appId) => ByAppId.TryGetValue(appId, out var n) ? n : $"AppID {appId}";

    public static IReadOnlyCollection<KeyValuePair<int, string>> All => ByAppId;

    /// <summary>High-value tiers worth flagging as "this account has real content".</summary>
    private static readonly HashSet<int> PremiumTier = new()
    {
        1245620, 1091500, 1238810, 2358720, 1817070, 1888930, 2050650, 990080, 1145360,
    };

    public static bool IsPremium(int appId) => PremiumTier.Contains(appId);

    public static int NotableCount(IEnumerable<OwnedGame> games)
        => games.Count(g => IsNotable(g.AppId));
}
