using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamForge.Core.Auth;
using SteamForge.Core.Combos;
using SteamForge.Core.Engine;
using SteamForge.Core.Models;
using SteamForge.Core.Output;
using SteamForge.Core.Proxy;

namespace SteamForge.App.ViewModels;

public sealed class ResultRow
{
    public string Username { get; init; } = "";
    public string Status { get; init; } = "";
    public string Guard { get; init; } = "";
    public string SteamId { get; init; } = "";
    public string Persona { get; init; } = "";
    public string Country { get; init; } = "";
    public string Level { get; init; } = "";
    public string Games { get; init; } = "";
    public string Notable { get; init; } = "";
    public string Wallet { get; init; } = "";
    public string Bans { get; init; } = "";
    public string Elapsed { get; init; } = "";
}

public sealed partial class MainViewModel : ObservableObject
{
    // ---- settings ----
    [ObservableProperty] private string _comboPath = "";
    [ObservableProperty] private string _proxyPath = "";
    [ObservableProperty] private string _outputDir = "output";
    [ObservableProperty] private int _workers = 30;
    [ObservableProperty] private int _maxRetries = 3;
    [ObservableProperty] private double _delayMs = 400;
    [ObservableProperty] private int _requestTimeout = 20;
    [ObservableProperty] private bool _useProxy;
    [ObservableProperty] private bool _enrich = true;
    [ObservableProperty] private bool _enrichGuardLocked = true;
    [ObservableProperty] private bool _notableFilter;
    [ObservableProperty] private int _minNotable = 1;
    [ObservableProperty] private int _minGames;
    [ObservableProperty] private int _minLevel;
    [ObservableProperty] private bool _showInvalid;

    // modes
    [ObservableProperty] private int _modeIndex; // 0 protobuf, 1 json, 2 native
    public string[] Modes { get; } =
    {
        "Web API · protobuf",
        "Web API · JSON",
        "Native · SteamKit2 (CM)",
    };

    // ---- run state ----
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progressValue;

    [ObservableProperty] private int _totalCombos;
    [ObservableProperty] private int _processed;
    [ObservableProperty] private int _valid;
    [ObservableProperty] private int _guardLocked;
    [ObservableProperty] private int _invalid;
    [ObservableProperty] private int _notFound;
    [ObservableProperty] private int _rateLimited;
    [ObservableProperty] private int _errors;
    [ObservableProperty] private int _retries;
    [ObservableProperty] private int _filtered;

    // proxy fetch state
    [ObservableProperty] private bool _isFetching;
    [ObservableProperty] private string _proxyStatus = "No proxies loaded.";
    // A fetch swaps _pool out from under the next run and an in-flight engine holds its
    // own reference, so the one-shot must be dark while a run owns the pool.
    [ObservableProperty] private bool _canFetchProxy = true;

    partial void OnIsFetchingChanged(bool value) => CanFetchProxy = !value && !IsRunning;
    partial void OnIsRunningChanged(bool value) => CanFetchProxy = !IsFetching && !value;

    public ObservableCollection<ResultRow> Rows { get; } = new();
    public ObservableCollection<ProxyRow> ProxyRows { get; } = new();

    private ProxyPool? _pool;
    private CancellationTokenSource? _cts;

    // Free lists currently hand over ~75k unique candidates, and most dead entries hang on
    // until the 12s probe ceiling instead of failing fast — measured 14.5k probes in 606s at
    // 256 wide, i.e. ~10.6s average per probe, which is timeout-bound, not bandwidth-bound.
    // Concurrency is therefore the only lever: 512 halves the wall clock to ~26 min while
    // staying under the ephemeral-port budget (~48/s x 2min TIME_WAIT = 5.8k of 16k ports).
    private const int ProxyTestConcurrency = 512;

    // The pool only needs to be big enough to carry a run when this many survivors have
    // landed. Measured: 100 alive at ~25s with the sweep at 512. Waiting for the full
    // 74,997-candidate sweep before flipping the switch would make "fetch, test, use" a
    // half-hour promise, and the engine keeps its own pool reference anyway.
    private const int AutoUseThreshold = 100;
    private CheckEngine? _engine;
    private bool _uiFlushPending;
    private readonly List<ResultRow> _pendingRows = new();
    private readonly object _rowLock = new();

    public MainViewModel()
    {
        ProxyRows.Add(new ProxyRow { Display = "Direct (no proxy)", Scheme = "-", Health = "—" });
    }

    // ---- proxy ----

    // Sidebar action: fill the pool, leave the mode switch where the user put it.
    [RelayCommand]
    private Task FetchProxiesAsync() => FetchCoreAsync(enableWhenReady: false);

    /// <summary>
    /// One press for the whole chain: pull the public free lists, probe every candidate
    /// against Steam's own auth endpoint, install the survivors and switch the run onto
    /// them. The checkbox ending up ON is the point of the button — a fetched pool that
    /// nobody is using still runs direct, which reads as "the proxy did nothing".
    /// </summary>
    [RelayCommand]
    private Task FetchAndUseProxiesAsync() =>
        IsRunning ? Task.CompletedTask : FetchCoreAsync(enableWhenReady: true);

    private async Task FetchCoreAsync(bool enableWhenReady)
    {
        if (IsFetching) return;
        IsFetching = true;
        try
        {
            ProxyStatus = "Fetching free proxy lists…";
            var result = await ProxyFetcher.FetchAsync(includeHttp: true, includeSocks5: true);
            int fetched = result.Proxies.Count;
            int sources = result.Report.Count(r => r.Error is null);

            if (fetched == 0)
            {
                ProxyStatus = $"No proxies came back from {sources} sources — nothing to test.";
                return;
            }

            ProxyStatus = $"Fetched {fetched:N0} candidates from {sources} sources. Testing against Steam…";

            // The pool goes in before the first probe, not after the last one. That is what
            // makes the panel live: every survivor lands in the pool immediately, the grid
            // repaints from the pool on a wall-clock throttle, and a run started mid-sweep
            // rides whatever has already been proven. The engine keeps the reference it was
            // constructed with, so a sweep that finishes late only shapes the next Start.
            var fresh = new ProxyPool();
            _pool = fresh;
            RefreshProxySnapshot();

            int tested = 0, aliveCount = 0, autoOn = 0;
            long lastUiMs = 0;

            // Probe order follows list order otherwise: the public lists are grouped by
            // provider, so every early survivor came from the same /24 and the panel read as
            // one host repeated down the column. One Fisher-Yates pass spreads the sample
            // across all nine lists from the first probe on.
            var candidates = new List<ProxyEntry>(result.Proxies);
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            await ProxyTester.FilterAliveAsync(candidates, concurrency: ProxyTestConcurrency, onEach: o =>
            {
                int done = Interlocked.Increment(ref tested);
                int a = 0;
                if (o.IsAlive)
                {
                    fresh.Add(o.Proxy);
                    a = Interlocked.Increment(ref aliveCount);
                }

                // One-shot path: the pool is usable long before the sweep is over, so the
                // switch flips at the threshold instead of at the last candidate. Measured
                // ~25s to 100 alive; waiting for 74,997 probes would make "fetch, test, use"
                // a half-hour promise and IsFetching would hold both buttons hostage.
                if (enableWhenReady && a >= AutoUseThreshold &&
                    Interlocked.Exchange(ref autoOn, 1) == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        UseProxy = true;
                        ProxyStatus = $"Pool: {a:N0} alive — proxy ON, sweep still running.";
                    }, DispatcherPriority.Background);
                }

                // Every 100th outcome, not every outcome: a 75,000-candidate sweep must not
                // queue 75,000 status posts behind a UI thread that is already painting rows.
                if (done % 100 == 0 || done == fetched)
                {
                    int aliveNow = Volatile.Read(ref aliveCount);
                    Dispatcher.UIThread.Post(
                        () => ProxyStatus = $"Testing {done:N0}/{fetched:N0}… {aliveNow} alive" +
                                             (UseProxy ? " · proxy ON" : ""),
                        DispatcherPriority.Background);
                }

                if (!o.IsAlive) return;

                // Snapshot on a 400ms clock instead of per proxy: at a high alive rate a
                // rebuild per survivor would spend the sweep redrawing instead of testing.
                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref lastUiMs);
                if (now - last >= 400 && Interlocked.CompareExchange(ref lastUiMs, now, last) == last)
                    Dispatcher.UIThread.Post(RefreshProxySnapshot, DispatcherPriority.Background);
            });

            int aliveTotal = aliveCount;
            RefreshProxySnapshot();

            if (aliveTotal == 0)
            {
                ProxyStatus = $"Tested {fetched:N0}, 0 alive — the pool is empty." +
                              (UseProxy ? " Proxy stays on; Start will refuse until a pool exists."
                                        : " Nothing to use.");
                return;
            }

            if (enableWhenReady) UseProxy = true;

            ProxyStatus = $"Pool: {aliveTotal:N0}/{fetched:N0} alive." +
                          (UseProxy ? " Proxy ON — ready to start." : " Ready.");
        }
        catch (Exception ex)
        {
            ProxyStatus = $"Fetch failed: {ex.Message}";
        }
        finally
        {
            IsFetching = false;
        }
    }

    [RelayCommand]
    private void LoadProxyFile()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ProxyPath) || !System.IO.File.Exists(ProxyPath))
            {
                ProxyStatus = "Proxy file not found.";
                return;
            }
            var entries = ProxyPool.ParseFile(ProxyPath);
            _pool = new ProxyPool();
            _pool.AddRange(entries);
            RefreshProxySnapshot();
            ProxyStatus = $"Loaded {entries.Count} proxies from file.";
        }
        catch (Exception ex)
        {
            ProxyStatus = $"Load failed: {ex.Message}";
        }
    }

    private void RefreshProxySnapshot()
    {
        if (_pool is null) return;
        var snap = _pool.Snapshot();
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            ProxyRows.Clear();
            if (snap.Entries.Count == 0)
            {
                ProxyRows.Add(new ProxyRow { Display = "No proxies in pool.", Scheme = "-", Health = "—" });
                return;
            }
            foreach (var e in snap.Entries.Take(200))
            {
                ProxyRows.Add(new ProxyRow
                {
                    Display = e.Display,
                    Scheme = e.SchemeToken,
                    Health = e.IsDead ? "dead"
                        : e.IsCoolingDown ? $"cool {e.CooldownUntil:HH:mm:ss}"
                        : $"{e.Successes}✓ / {e.Failures}✗",
                    Latency = e.AvgLatencyMs >= 0 ? $"{e.AvgLatencyMs} ms" : "—",
                    SuccessRate = $"{e.SuccessRate:P0}",
                });
            }
        });
    }

    // ---- run ----

    [RelayCommand]
    private void Start()
    {
        if (IsRunning) return;

        // load combos
        if (string.IsNullOrWhiteSpace(ComboPath) || !System.IO.File.Exists(ComboPath))
        {
            StatusText = "Combo file not found.";
            return;
        }

        ComboParseReport report;
        try
        {
            report = ComboParser.ParseFile(ComboPath);
        }
        catch (Exception ex)
        {
            StatusText = $"Combo parse failed: {ex.Message}";
            return;
        }

        if (report.Combos.Count == 0)
        {
            StatusText = "No valid combos parsed.";
            return;
        }

        if (UseProxy && (_pool is null || _pool.Count == 0))
        {
            StatusText = "Proxy enabled but pool is empty — fetch or load proxies first.";
            return;
        }

        // A relative output dir follows the executable, not whatever directory happened to
        // launch it: double-clicking SteamForge.exe and starting it from a script must land
        // the results in the same place. Directory is created here so the session file, the
        // writer and the resume point all agree on one root.
        var outputDir = ResolveOutputDir(OutputDir);
        OutputDir = outputDir;
        Directory.CreateDirectory(outputDir);

        var mode = ModeIndex switch
        {
            1 => AuthMode.WebApiJson,
            2 => AuthMode.NativeSteamKit,
            _ => AuthMode.WebApiProtobuf,
        };

        var fingerprint = SessionState.ComputeFingerprint(ComboPath);

        // Never null: a first run opens a session immediately, so the engine's 5-second
        // flush leaves a resume point on disk before anything can kill the process.
        var session = SessionState.TryLoad(outputDir, fingerprint)
                      ?? new SessionState { Fingerprint = fingerprint, StartedUtc = DateTime.UtcNow };

        var alreadyDone = session.DoneCount;

        var writer = new ResultWriter(new OutputOptions { OutputDirectory = outputDir });

        var opts = new EngineOptions
        {
            Combos = report.Combos,
            Mode = mode,
            Workers = Math.Max(1, Workers),
            MaxRetries = Math.Max(0, MaxRetries),
            SubmitDelay = TimeSpan.FromMilliseconds(Math.Max(0, DelayMs)),
            RequestTimeout = TimeSpan.FromSeconds(Math.Max(5, RequestTimeout)),
            Pool = UseProxy ? _pool : null,
            Enrich = Enrich,
            EnrichGuardLocked = EnrichGuardLocked,
            NotableFilter = NotableFilter,
            MinNotableGames = MinNotable,
            MinGameCount = MinGames,
            MinLevel = MinLevel,
            Writer = writer,
            Session = session,
            OutputDirectory = outputDir,
            PauseGate = () => !IsPaused,
        };

        _engine = new CheckEngine(opts);
        _engine.OnResult += OnEngineResult;
        _engine.OnNotice += OnEngineNotice;

        TotalCombos = report.Combos.Count;
        ResetCounters();
        // Engine seeds its stats from a resume in its constructor, so a resumed run shows the
        // previous run's totals immediately instead of waiting for the first new result.
        SyncCounters();
        Rows.Clear();

        _cts = new CancellationTokenSource();
        IsRunning = true;
        IsPaused = false;
        StatusText = $"Running — {report.Combos.Count} combos, mode {Modes[ModeIndex]}, {Workers} workers" +
                     (UseProxy ? ", proxy on" : ", direct") +
                     (alreadyDone > 0 ? $", resuming ({alreadyDone} done)" : "") +
                     $" · output {outputDir}.";

        var token = _cts.Token;
        var engine = _engine;
        var w = writer;

        _ = Task.Run(async () =>
        {
            var completed = false;
            try
            {
                await engine.RunAsync(token).ConfigureAwait(false);
                completed = true;
            }
            catch (OperationCanceledException)
            {
                // Stopped by user: the session file deliberately stays on disk.
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => StatusText = $"Engine error: {ex.Message}");
            }
            finally
            {
                // A run that reached the end of the file has nothing left to resume, and a
                // session left behind would make the next Start skip every account in it.
                if (completed) session.Delete(outputDir);

                w.Dispose();
                _engine.OnResult -= OnEngineResult;
                _engine.OnNotice -= OnEngineNotice;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsRunning = false;
                    IsPaused = false;
                    FlushPendingRows();
                    // Counters only refresh on a result event; a resume that had nothing left
                    // to check fires none, so pull the engine's final state directly.
                    SyncCounters();
                    StatusText = completed
                        ? $"Done. {Valid} valid · {GuardLocked} guard · {Invalid + NotFound} bad · " +
                          $"{RateLimited} limited · {Errors} errors."
                        : $"Stopped — {engine.Stats.Processed} of {engine.Stats.Total} checked. " +
                          "Start again to resume from the session file.";
                    RefreshProxySnapshot();
                });
            }
        }, token);
    }

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        StatusText = "Stopping…";
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    // Engine notices (proxy retirement, rate-limit backoff) arrive on worker threads. They go
    // through the same coalesced flush as rows so a burst cannot flood the dispatcher.
    private void OnEngineNotice(string message) =>
        Dispatcher.UIThread.Post(() => ProxyStatus = message, DispatcherPriority.Background);

    private void OnEngineResult(CheckResult r)
    {        bool show = r.Status switch
        {
            CheckStatus.Valid => true,
            CheckStatus.GuardLocked => true,
            _ => ShowInvalid,
        };

        if (show)
        {
            lock (_rowLock) _pendingRows.Add(ToRow(r));
        }

        // Coalesce UI updates: one dispatch per 150ms regardless of engine rate, so a
        // 200-worker run does not drown the dispatcher.
        if (!_uiFlushPending && Interlocked.CompareExchange(ref _flushFlag, 1, 0) == 0)
        {
            _uiFlushPending = true;
            _ = Dispatcher.UIThread.InvokeAsync(() =>
            {
                FlushPendingRows();
                SyncCounters();
                _uiFlushPending = false;
                Interlocked.Exchange(ref _flushFlag, 0);
            }, DispatcherPriority.Background);
        }
    }

    private int _flushFlag;

    private void FlushPendingRows()
    {
        List<ResultRow> batch;
        lock (_rowLock)
        {
            if (_pendingRows.Count == 0) return;
            batch = new List<ResultRow>(_pendingRows);
            _pendingRows.Clear();
        }

        foreach (var row in batch) Rows.Add(row);
        while (Rows.Count > 5000) Rows.RemoveAt(0);
    }

    private void SyncCounters()
    {
        // The engine owns the counts — duplicating them here meant two sources of truth that
        // could drift, and it silently left Retries/Filtered pinned at zero.
        if (_engine is { } e)
        {
            var s = e.Stats;
            Processed = s.Processed;
            Valid = s.Valid;
            GuardLocked = s.GuardLocked;
            Invalid = s.Invalid;
            NotFound = s.NotFound;
            RateLimited = s.RateLimited;
            Errors = s.Errors;
            Retries = s.Retries;
            Filtered = s.Filtered;
        }

        ProgressValue = TotalCombos == 0 ? 0 : (double)Processed / TotalCombos * 100.0;
        ProgressText = $"{Processed:N0} / {TotalCombos:N0}  ({ProgressValue:0.0}%)";
    }

    private static string ResolveOutputDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return AppContext.BaseDirectory;
        return Path.IsPathRooted(dir)
            ? Path.GetFullPath(dir)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dir));
    }

    private void ResetCounters()
    {
        Processed = Valid = GuardLocked = Invalid = NotFound = RateLimited = Errors = Retries = Filtered = 0;
        ProgressValue = 0;
        ProgressText = $"0 / {TotalCombos:N0}";
    }

    private static ResultRow ToRow(CheckResult r)
    {
        var p = r.Profile;
        return new ResultRow
        {
            Username = r.Combo.Username,
            Status = r.Status.ToString(),
            Guard = r.Guard == GuardKind.None ? "" : r.Guard.ToString(),
            SteamId = r.SteamId != 0 ? r.SteamId.ToString() : "",
            Persona = p?.PersonaName ?? "",
            Country = p?.CountryCode ?? "",
            Level = p is { Level: >= 0 } ? p.Level.ToString() : "",
            Games = p is { GameCount: >= 0 } ? p.GameCount.ToString() : "",
            Notable = p is null ? "" : Core.Models.NotableGames.NotableCount(p.Games).ToString(),
            Wallet = string.IsNullOrEmpty(p?.WalletBalance) ? "" : $"{p!.WalletBalance} {p.WalletCurrency}".Trim(),
            Bans = p is null ? "" : $"{p.VacBans}V/{p.GameBans}G{(p.TradeBanned ? "/T" : "")}",
            Elapsed = $"{r.Elapsed.TotalSeconds:0.0}s",
        };
    }

    public void Shutdown()
    {
        _cts?.Cancel();
    }
}

public sealed class ProxyRow
{
    public string Display { get; set; } = "";
    public string Scheme { get; set; } = "";
    public string Health { get; set; } = "";
    public string Latency { get; set; } = "—";
    public string SuccessRate { get; set; } = "—";
}
