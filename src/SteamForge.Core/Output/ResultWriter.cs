using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using SteamForge.Core.Models;

namespace SteamForge.Core.Output;

public sealed class OutputOptions
{
    public required string OutputDirectory { get; init; }
    public bool WriteHitsTxt { get; init; } = true;
    public bool WriteJsonl { get; init; } = true;
    public bool WriteCsv { get; init; } = true;
    public bool WriteInvalid { get; init; } = true;
    public int MaxGamesInTxt { get; init; } = 40;
}

/// <summary>
/// Fan-out writer for check results. Every sink is independent: a full disk on the CSV
/// stream must not stop hits.txt from receiving the line that just landed.
/// Writes are append-only and flushed per verdict so a crash mid-run keeps everything
/// already decided.
/// </summary>
public sealed class ResultWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly OutputOptions _opts;
    private readonly object _gate = new();

    private StreamWriter? _hits;
    private StreamWriter? _invalid;
    private StreamWriter? _jsonl;
    private StreamWriter? _csv;
    private bool _csvHeaderWritten;
    private bool _disposed;

    public string HitsPath { get; }
    public string InvalidPath { get; }
    public string JsonlPath { get; }
    public string CsvPath { get; }

    public ResultWriter(OutputOptions opts)
    {
        _opts = opts;
        Directory.CreateDirectory(opts.OutputDirectory);

        HitsPath = Path.Combine(opts.OutputDirectory, "hits.txt");
        InvalidPath = Path.Combine(opts.OutputDirectory, "invalid.txt");
        JsonlPath = Path.Combine(opts.OutputDirectory, "results.jsonl");
        CsvPath = Path.Combine(opts.OutputDirectory, "results.csv");

        if (opts.WriteHitsTxt) _hits = NewStream(HitsPath);
        if (opts.WriteInvalid) _invalid = NewStream(InvalidPath);
        if (opts.WriteJsonl) _jsonl = NewStream(JsonlPath);
        if (opts.WriteCsv) _csv = NewStream(CsvPath);
    }

    private static StreamWriter NewStream(string path) => new(
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
        new System.Text.UTF8Encoding(false))
    { AutoFlush = true };

    public void Write(CheckResult result)
    {
        if (_disposed) return;

        lock (_gate)
        {
            try
            {
                switch (result.Status)
                {
                    case CheckStatus.Valid:
                    case CheckStatus.GuardLocked:
                        _hits?.WriteLine(result.SummaryLine());
                        AppendDetails(result);
                        break;

                    case CheckStatus.Invalid:
                    case CheckStatus.NotFound:
                        _invalid?.WriteLine($"{result.Combo.Username}:{result.Combo.Password} | {result.Status}");
                        break;
                }

                _jsonl?.WriteLine(JsonSerializer.Serialize(ToJson(result), JsonOptions));
                AppendCsv(result);
            }
            catch
            {
                // A single unwritable sink must never abort the run.
            }
        }
    }

    private void AppendDetails(CheckResult result)
    {
        if (_hits is null || result.Profile is null) return;
        var p = result.Profile;

        if (p.Games.Count == 0) return;

        var shownCount = p.GameCount >= 0 ? p.GameCount : p.Games.Count;
        _hits.WriteLine($"   Games ({shownCount} total):");
        int shown = 0;
        foreach (var g in p.Games)
        {
            if (shown >= _opts.MaxGamesInTxt) break;
            var tag = NotableGames.IsNotable(g.AppId) ? " *" : string.Empty;
            _hits.WriteLine($"     {shown + 1,3}. {g.Name}{tag}  [{FormatPlaytime(g.PlaytimeForeverMinutes)}]");
            shown++;
        }
        if (p.Games.Count > shown)
            _hits.WriteLine($"     ... and {p.Games.Count - shown} more");
        _hits.WriteLine();
    }

    private void AppendCsv(CheckResult result)
    {
        if (_csv is null) return;

        if (!_csvHeaderWritten)
        {
            _csv.WriteLine("timestamp_utc,username,password,status,guard,steamid,persona,level," +
                           "game_count,country,wallet_balance,wallet_currency,vac_bans,game_bans," +
                           "trade_banned,limited,notable_count,proxy,mode,attempts,elapsed_ms,reason");
            _csvHeaderWritten = true;
        }

        var p = result.Profile;
        _csv.WriteLine(string.Join(',',
            Csv(result.TimestampUtc.ToString("o")),
            Csv(result.Combo.Username),
            Csv(result.Combo.Password),
            Csv(result.Status.ToString()),
            Csv(result.Guard.ToString()),
            result.SteamId != 0 ? result.SteamId.ToString() : "",
            Csv(p?.PersonaName),
            (p is { Level: >= 0 } ? p.Level.ToString() : ""),
            (p is { GameCount: >= 0 } ? p.GameCount.ToString() : ""),
            Csv(p?.CountryCode),
            Csv(p?.WalletBalance),
            Csv(p?.WalletCurrency),
            p is not null ? p.VacBans.ToString() : "",
            p is not null ? p.GameBans.ToString() : "",
            p is not null ? p.TradeBanned.ToString().ToLowerInvariant() : "",
            p is not null ? p.LimitedAccount.ToString().ToLowerInvariant() : "",
            p is not null ? NotableGames.NotableCount(p.Games).ToString() : "",
            Csv(result.ProxyUsed),
            Csv(result.Mode.ToString()),
            result.Attempts.ToString(),
            result.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
            Csv(result.FailureReason)));
    }

    private static object ToJson(CheckResult r)
    {
        var p = r.Profile;
        return new
        {
            timestamp_utc = r.TimestampUtc.ToString("o"),
            username = r.Combo.Username,
            password = r.Combo.Password,
            status = r.Status,
            guard = r.Guard,
            steamid = r.SteamId != 0 ? r.SteamId.ToString() : null,
            proxy = r.ProxyUsed,
            mode = r.Mode,
            attempts = r.Attempts,
            elapsed_ms = (long)r.Elapsed.TotalMilliseconds,
            reason = r.FailureReason,
            persona = p?.PersonaName,
            country = p?.CountryCode,
            level = p?.Level,
            game_count = p?.GameCount,
            wallet_balance = p?.WalletBalance,
            wallet_currency = p?.WalletCurrency,
            vac_bans = p?.VacBans,
            game_bans = p?.GameBans,
            days_since_last_ban = p?.DaysSinceLastBan,
            trade_banned = p?.TradeBanned,
            community_banned = p?.CommunityBanned,
            limited = p?.LimitedAccount,
            account_created_utc = (p is not null && p.AccountCreatedUtc != default) ? p.AccountCreatedUtc.ToString("o") : null,
            profile_public = p?.ProfilePublic,
            notable = p is null ? null : p.Games.Where(g => NotableGames.IsNotable(g.AppId))
                .Select(g => new { g.AppId, g.Name, minutes = g.PlaytimeForeverMinutes }),
            games = p is null ? null : p.Games.Take(500).Select(g => new
            {
                g.AppId,
                g.Name,
                minutes = g.PlaytimeForeverMinutes,
            }),
        };
    }

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public static string FormatPlaytime(int minutes)
    {
        if (minutes <= 0) return "0m";
        if (minutes < 60) return $"{minutes}m";
        int hours = minutes / 60;
        int mins = minutes % 60;
        return hours >= 1000 ? $"{hours:N0}h" : $"{hours}h {mins}m";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _hits?.Dispose();
            _invalid?.Dispose();
            _jsonl?.Dispose();
            _csv?.Dispose();
        }
    }
}
