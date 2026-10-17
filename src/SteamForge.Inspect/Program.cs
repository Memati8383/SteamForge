using System.Text;
using SteamForge.Core.Combos;

namespace SteamForge.Tests;

/// <summary>
/// Parses a real combo file and reports exactly what would be submitted to Steam.
/// Used to verify parsing against a real-world list before any live traffic.
/// </summary>
internal static class Inspect
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var path = args.Length > 0 ? args[0] : @"C:\Users\USER\Desktop\1.txt";
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"not found: {path}");
            return 2;
        }

        var report = ComboParser.ParseFile(path);

        Console.WriteLine($"file          : {path}");
        Console.WriteLine($"total lines   : {report.TotalLines}");
        Console.WriteLine($"combos        : {report.Combos.Count}");
        Console.WriteLine($"blank         : {report.SkippedBlank}");
        Console.WriteLine($"comments      : {report.SkippedComment}");
        Console.WriteLine($"malformed     : {report.SkippedMalformed}");
        Console.WriteLine($"dupes removed : {report.DuplicatesRemoved}");
        Console.WriteLine($"reversed fix  : {report.ReversedFixed}");
        Console.WriteLine();

        // Any password still carrying trailer text means the credential split failed and every
        // such account would be reported Invalid while actually being good.
        var polluted = report.Combos
            .Where(c => c.Password.Contains("GamesList", StringComparison.OrdinalIgnoreCase)
                        || c.Password.Contains(" | ", StringComparison.Ordinal))
            .ToList();

        Console.WriteLine($"passwords polluted with trailer : {polluted.Count}");
        foreach (var c in polluted.Take(10))
            Console.WriteLine($"   !! {c.Username} -> '{c.Password}'");

        var withNote = report.Combos.Count(c => c.Note is not null);
        Console.WriteLine($"rows with captured Note         : {withNote}");
        Console.WriteLine();

        Console.WriteLine("first 8 parsed credentials (this is what gets submitted):");
        foreach (var c in report.Combos.Take(8))
            Console.WriteLine($"  L{c.LineNumber,-4} {c.Username}:{c.Password}");

        if (report.SkippedMalformed > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"first {Math.Min(5, report.SkippedMalformed)} malformed lines:");
            var lines = File.ReadAllLines(path);
            int shown = 0;
            for (int i = 0; i < lines.Length && shown < 5; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith('#') || t.StartsWith("//") || t.StartsWith(";")) continue;
                var strip = t.IndexOf(" | ", StringComparison.Ordinal);
                if (strip >= 0) t = t[..strip].Trim();
                if (!t.Contains(':') || t.StartsWith(':') || t.EndsWith(':'))
                {
                    Console.WriteLine($"  L{i + 1}: {lines[i]}");
                    shown++;
                }
            }
        }

        // Rough duplicate-username detector outside the parser, to cross-check its count.
        var names = report.Combos.Select(c => c.Username.ToLowerInvariant()).ToList();
        Console.WriteLine();
        Console.WriteLine($"unique usernames (independent check): {names.Distinct().Count()}");

        return polluted.Count == 0 ? 0 : 1;
    }
}
