using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamForge.Core.Models;

namespace SteamForge.Core.Auth;

/// <summary>
/// Native CM-protocol auth via SteamKit2. Where the web-API path speaks HTTP to a storefront,
/// this opens a real Steam client connection to the Connection Manager and authenticates the
/// way the desktop client does.
///
/// Trade-offs, deliberately:
///   + Indistinguishable from a genuine Steam client at the protocol layer.
///   + Not in the storefront's HTTP rate-limit bucket.
///   − One TCP connection per attempt, so throughput is far below the HTTP path.
///   − Connection setup costs a few hundred ms before any auth traffic.
/// Right tool for high-value, low-volume work — not for grinding a large combo list.
/// The engine selects it explicitly, never silently.
///
/// SteamKit2's connection is callback-driven, so a CallbackManager pump loop runs for the
/// lifetime of each attempt. The loop is what actually delivers Connected/Disconnected;
/// awaiting a TaskCompletionSource without pumping it would never complete.
/// </summary>
public sealed class NativeSteamAuth : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GuardWaitTimeout = TimeSpan.FromSeconds(30);

    public async Task<CredentialOutcome> CheckAsync(
        string accountName,
        string password,
        string? twoFactorCode = null,
        CancellationToken ct = default)
    {
        // SteamKit2 needs a live thread pumping callbacks; run that off the auth task so
        // the async flow can await real results.
        return await Task.Run(async () =>
        {
            SteamClient? client = null;
            CallbackManager? manager = null;
            var pumpRunning = true;

            try
            {
                client = new SteamClient();
                manager = new CallbackManager(client);

                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
                manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
                {
                    pumpRunning = false;
                    connected.TrySetCanceled();
                });

                // Kick off the callback pump before connecting so no callback is missed.
                var pumpTask = Task.Run(() =>
                {
                    while (pumpRunning && !ct.IsCancellationRequested)
                    {
                        try { manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
                        catch { break; }
                    }
                }, ct);

                client.Connect();

                var connectWait = Task.Delay(ConnectTimeout, ct);
                var first = await Task.WhenAny(connected.Task, connectWait).ConfigureAwait(false);
                if (first != connected.Task)
                {
                    pumpRunning = false;
                    return new CredentialOutcome { Status = CheckStatus.Error, Reason = "CM connect timeout" };
                }

                if (ct.IsCancellationRequested)
                {
                    pumpRunning = false;
                    return new CredentialOutcome { Status = CheckStatus.Error, Reason = "cancelled" };
                }

                CredentialsAuthSession session;
                try
                {
                    session = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                    {
                        Username = accountName,
                        Password = password,
                        IsPersistentSession = false,
                        PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                        WebsiteID = "Client",
                        DeviceFriendlyName = "Steam Client",
                        // No authenticator: a guard-required account surfaces as an exception
                        // rather than blocking on a code we cannot obtain.
                        Authenticator = twoFactorCode is null ? null : new FixedAuthenticator(twoFactorCode),
                    }).ConfigureAwait(false);
                }
                catch (AuthenticationException ex)
                {
                    pumpRunning = false;
                    return ClassifyAuthException(ex);
                }
                catch (InvalidOperationException ex)
                {
                    pumpRunning = false;
                    return ClassifyGuardInvalidOperation(ex);
                }
                catch (Exception ex)
                {
                    pumpRunning = false;
                    return new CredentialOutcome { Status = CheckStatus.Error, Reason = Trim(ex.Message) };
                }

                if (session is null)
                {
                    pumpRunning = false;
                    return new CredentialOutcome { Status = CheckStatus.Invalid, Reason = "no auth session" };
                }

                // Populated as soon as credentials are accepted, so a guard stop still
                // carries an ID for the keyless profile path.
                ulong steamId = SafeSteamId(session);

                // PollingWaitForResultAsync inspects the allowed confirmations:
                //   no guard          → polls and returns an AuthPollResult
                //   code-based guard  → throws when Authenticator is null
                //   device confirm    → polls until mobile approval (bounded below)
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(GuardWaitTimeout);

                AuthPollResult poll;
                try
                {
                    poll = await session.PollingWaitForResultAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    pumpRunning = false;
                    return WithSteamId(ClassifyGuardInvalidOperation(ex), steamId);
                }
                catch (OperationCanceledException)
                {
                    pumpRunning = false;
                    return new CredentialOutcome
                    {
                        Status = CheckStatus.GuardLocked,
                        Guard = GuardKind.DeviceConfirmation,
                        Reason = "device confirmation pending",
                        SteamId = steamId,
                    };
                }
                catch (AuthenticationException ex)
                {
                    pumpRunning = false;
                    return WithSteamId(ClassifyAuthException(ex), steamId);
                }

                if (poll is null || string.IsNullOrEmpty(poll.RefreshToken))
                {
                    pumpRunning = false;
                    return new CredentialOutcome { Status = CheckStatus.Error, Reason = "poll returned no refresh token" };
                }

                // Refresh: SteamID is only guaranteed once the poll completes.
                steamId = SafeSteamId(session);

                pumpRunning = false;
                return new CredentialOutcome
                {
                    Status = CheckStatus.Valid,
                    Guard = GuardKind.None,
                    SteamId = steamId,
                    AccessToken = poll.AccessToken,
                    RefreshToken = poll.RefreshToken,
                };
            }
            catch (OperationCanceledException)
            {
                pumpRunning = false;
                return new CredentialOutcome { Status = CheckStatus.Error, Reason = "cancelled" };
            }
            catch (Exception ex)
            {
                pumpRunning = false;
                return new CredentialOutcome { Status = CheckStatus.Error, Reason = Trim(ex.Message) };
            }
            finally
            {
                pumpRunning = false;
                try { client?.Disconnect(); } catch { /* teardown is best-effort */ }
            }
        }, ct).ConfigureAwait(false);
    }

    // ---- Classification ----

    /// <summary>SteamID is null until credentials are accepted, and never throws into the auth flow.</summary>
    private static ulong SafeSteamId(CredentialsAuthSession session)
    {
        try { return session.SteamID.ConvertToUInt64(); }
        catch { return 0; }
    }

    private static CredentialOutcome WithSteamId(CredentialOutcome outcome, ulong steamId)
    {
        if (steamId != 0 && outcome.SteamId == 0) outcome.SteamId = steamId;
        return outcome;
    }

    private static CredentialOutcome ClassifyAuthException(AuthenticationException ex)
    {
        var result = ex.Result;
        return result switch
        {
            EResult.InvalidPassword or EResult.InvalidParam or EResult.InvalidLoginAuthCode =>
                new CredentialOutcome { Status = CheckStatus.Invalid, Reason = $"EResult {result}" },

            EResult.AccountNotFound or EResult.InvalidSteamID =>
                new CredentialOutcome { Status = CheckStatus.NotFound, Reason = $"EResult {result}" },

            EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle or EResult.TryAnotherCM =>
                new CredentialOutcome { Status = CheckStatus.RateLimited, Reason = $"EResult {result}" },

            EResult.Banned =>
                new CredentialOutcome { Status = CheckStatus.Invalid, Reason = $"EResult {result}" },

            EResult.TwoFactorCodeMismatch or EResult.TwoFactorActivationCodeMismatch =>
                new CredentialOutcome { Status = CheckStatus.GuardLocked, Guard = GuardKind.DeviceCode, Reason = $"EResult {result}" },

            _ => new CredentialOutcome { Status = CheckStatus.Error, Reason = $"EResult {result}" },
        };
    }

    private static CredentialOutcome ClassifyGuardInvalidOperation(InvalidOperationException ex)
    {
        var msg = ex.Message ?? string.Empty;

        if (msg.Contains("requires an authenticator", StringComparison.OrdinalIgnoreCase))
            return new CredentialOutcome { Status = CheckStatus.GuardLocked, Guard = GuardKind.DeviceCode, Reason = "guard requires code" };

        if (msg.Contains("no allowed confirmations", StringComparison.OrdinalIgnoreCase))
            return new CredentialOutcome { Status = CheckStatus.Error, Reason = "no confirmations offered" };

        return new CredentialOutcome { Status = CheckStatus.Error, Reason = Trim(msg) };
    }

    private static string Trim(string s)
    {
        var idx = s.IndexOfAny(new[] { '\r', '\n' });
        return idx >= 0 ? s[..idx] : s;
    }

    public void Dispose() { }

    /// <summary>Supplies a fixed Steam Guard code so a session can be completed in one shot.</summary>
    private sealed class FixedAuthenticator : IAuthenticator
    {
        private readonly string _code;
        public FixedAuthenticator(string code) => _code = code;

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            => Task.FromResult(_code);

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            => Task.FromResult(_code);

        public Task<bool> AcceptDeviceConfirmationAsync()
            => Task.FromResult(true);
    }
}
