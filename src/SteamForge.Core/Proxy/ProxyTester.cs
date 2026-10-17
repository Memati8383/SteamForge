using System.Diagnostics;
using System.Text;
using SteamForge.Core.Auth;

namespace SteamForge.Core.Proxy;

public sealed record ProxyTestOutcome(ProxyEntry Proxy, bool IsAlive, long LatencyMs, string? Detail);

/// <summary>
/// Validates a proxy against the exact endpoint the checker will call, not a generic
/// "can I reach the internet" probe. GetPasswordRSAPublicKey with a non-existent account
/// returns 200 + a JSON envelope in a few hundred ms and exercises the same TLS path,
/// the same Host header, and the same region Steam's edge will see for a real check.
/// </summary>
public static class ProxyTester
{
    private const string ProbeUrl =
        "https://api.steampowered.com/IAuthenticationService/GetPasswordRSAPublicKey/v1";

    public static async Task<ProxyTestOutcome> TestAsync(
        ProxyEntry proxy,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var fp = Fingerprint.Random();
        using var client = HttpFactory.CreateClient(proxy, timeout, fp);

        try
        {
            var body = "{\"account_name\":\"sf_probe_nonexistent_0042\"}";
            using var req = new HttpRequestMessage(HttpMethod.Get,
                ProbeUrl + "?origin=https%3A%2F%2Fstore.steampowered.com&input_json=" +
                Uri.EscapeDataString(body));

            ApplyHeaders(req, fp);

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            sw.Stop();

            if (resp.StatusCode is System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.TooManyRequests)
            {
                return new ProxyTestOutcome(proxy, false, sw.ElapsedMilliseconds,
                    $"HTTP {(int)resp.StatusCode}");
            }

            if (!resp.IsSuccessStatusCode)
                return new ProxyTestOutcome(proxy, false, sw.ElapsedMilliseconds,
                    $"HTTP {(int)resp.StatusCode}");

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // A live proxy returns a JSON envelope; a transparent-MITM or captive portal
            // returns HTML with a 200.
            if (!text.TrimStart().StartsWith('{'))
                return new ProxyTestOutcome(proxy, false, sw.ElapsedMilliseconds, "non-json body");

            proxy.MarkSuccess(sw.ElapsedMilliseconds);
            return new ProxyTestOutcome(proxy, true, sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            proxy.MarkFailure(TimeSpan.FromMinutes(5));
            return new ProxyTestOutcome(proxy, false, sw.ElapsedMilliseconds, "timeout");
        }
        catch (Exception ex)
        {
            proxy.MarkFailure(TimeSpan.FromMinutes(5));
            return new ProxyTestOutcome(proxy, false, sw.ElapsedMilliseconds, Shorten(ex.Message));
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Tests a batch concurrently and returns only the survivors.</summary>
    public static async Task<List<ProxyEntry>> FilterAliveAsync(
        IEnumerable<ProxyEntry> candidates,
        int concurrency,
        Action<ProxyTestOutcome>? onEach = null,
        CancellationToken ct = default)
    {
        var alive = new List<ProxyEntry>();
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));

        var tasks = candidates.Select(async p =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var outcome = await TestAsync(p, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
                onEach?.Invoke(outcome);
                if (outcome.IsAlive)
                {
                    lock (alive) alive.Add(p);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return alive;
    }

    private static void ApplyHeaders(HttpRequestMessage req, Fingerprint fp)
    {
        foreach (var kv in fp.ClientHints())
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        req.Headers.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-site");
    }

    private static string Shorten(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) { if (char.IsControl(c)) break; sb.Append(c); if (sb.Length > 60) break; }
        return sb.ToString();
    }
}
