using SteamForge.Core.Proto;

namespace SteamForge.Core.Auth;

/// <summary>EAuthSessionGuardType — what Steam will accept as the second factor.</summary>
public enum AuthConfirmationType
{
    Invalid = 0,
    None = 1,
    EmailCode = 2,
    DeviceCode = 3,
    DeviceConfirmation = 4,
}

/// <summary>
/// EAuthTokenPlatformType — the client we claim to be. MobileApp yields an oauth token usable
/// with IMobileAuthService, which is what the enrichment layer wants.
///
/// Values read from SteamKit2's generated EAuthTokenPlatformType, not from memory: Steam's real
/// ordering is SteamClient=1, WebBrowser=2, MobileApp=3. An off-by-one here silently claims to
/// be a browser, and Steam answers eresult 5 for every password.
/// </summary>
public enum AuthPlatformType
{
    Unknown = 0,
    SteamClient = 1,
    WebBrowser = 2,
    MobileApp = 3,
}

/// <summary>ESessionPersistence.</summary>
public enum AuthPersistence
{
    Invalid = -1,
    Ephemeral = 0,
    Persistent = 1,
}

public sealed class RsaKey
{
    public required string ModulusHex { get; init; }
    public required string ExponentHex { get; init; }
    public required string Timestamp { get; init; }
}

public sealed class BeginAuthResult
{
    public ulong ClientId { get; init; }
    public byte[] RequestId { get; init; } = Array.Empty<byte>();

    /// <summary>Seconds until the next poll. Steam sends this as a float (fixed32), not an int.</summary>
    public float Interval { get; init; }

    public ulong SteamId { get; init; }
    public List<AuthConfirmationType> Confirmations { get; init; } = new();
    public string? AgreementSessionUrl { get; init; }
    public bool HasWeakPassword { get; init; }
    public string? ExtendedErrorMessage { get; init; }
}

public sealed class PollAuthResult
{
    public ulong NewClientId { get; init; }
    public string? NewChallengeUrl { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? AccountName { get; init; }
    public string? NewGuardData { get; init; }
    public bool HadRemoteInteraction { get; init; }
}

/// <summary>
/// Field-number bindings for IAuthenticationService, matching steammessages_auth.steamclient.proto.
///
/// Every number and wire type below was read back from SteamKit2's generated serializer rather
/// than written from memory. That matters: the earlier hand-written bindings were confidently
/// wrong in a way that still produced well-formed protobuf, so Steam parsed the message, read
/// account_name out of the device_friendly_name slot, and answered eresult 5 for every
/// password — correct ones included. Well-formed and correct are different properties.
///
/// Valve adds fields; it does not renumber them, so these stay stable across client updates.
/// </summary>
public static class AuthMessages
{
    // ---- GetPasswordRSAPublicKey ----
    public static string BuildRsaKeyRequest(string accountName)
        => new ProtobufWriter().WriteString(1, accountName).ToBase64();

    public static RsaKey? ParseRsaKeyResponse(byte[] body)
    {
        var r = new ProtobufReader(body);
        string? mod = null, exp = null, ts = null;
        ProtoValue? v;
        while ((v = r.Next()) is not null)
        {
            switch (v.Value.FieldNumber)
            {
                case 1: mod = v.Value.AsString(); break;
                case 2: exp = v.Value.AsString(); break;
                // Steam sends timestamp as a varint. Reading it as a string yields "" and the
                // credential request then carries no timestamp at all.
                case 3: ts = AsStringLoose(v.Value); break;
            }
        }
        if (string.IsNullOrEmpty(mod) || string.IsNullOrEmpty(exp)) return null;
        return new RsaKey { ModulusHex = mod, ExponentHex = exp, Timestamp = string.IsNullOrEmpty(ts) ? "0" : ts };
    }

    /// <summary>
    /// Reads a field as text whether it arrived as a length-delimited string or a varint.
    /// Reading a varint as a string silently yields "" instead of throwing.
    /// </summary>
    private static string AsStringLoose(ProtoValue v)
    {
        if (v.Kind == ProtoField.Varint) return v.Varint.ToString();
        return v.AsString();
    }

    // ---- BeginAuthSessionViaCredentials ----
    //
    // [1] device_friendly_name  string
    // [2] account_name          string
    // [3] encrypted_password    string
    // [4] encryption_timestamp  varint
    // [5] remember_login        bool
    // [6] platform_type         enum
    // [7] persistence           enum
    // [8] website_id            string
    public static string BuildBeginAuthRequest(
        string accountName,
        string encryptedPassword,
        string encryptionTimestamp,
        AuthPlatformType platform,
        string deviceFriendlyName,
        bool rememberLogin,
        AuthPersistence persistence)
    {
        var w = new ProtobufWriter()
            .WriteString(1, deviceFriendlyName)
            .WriteString(2, accountName)
            .WriteString(3, encryptedPassword);

        if (ulong.TryParse(encryptionTimestamp, out var ts))
            w.WriteVarint(4, ts);
        else
            w.WriteString(4, encryptionTimestamp);

        w.WriteBool(5, rememberLogin)
            .WriteEnum(6, (int)platform)
            .WriteEnum(7, (int)persistence)
            .WriteString(8, "Store");

        return w.ToBase64();
    }

    // [1] client_id               varint
    // [2] request_id              bytes
    // [3] interval                float (fixed32)
    // [4] allowed_confirmations   message
    // [5] steamid                 varint
    // [6] weak_token              string
    // [7] agreement_session_url   string
    // [8] extended_error_message  string
    public static BeginAuthResult? ParseBeginAuthResponse(byte[] body)
    {
        var r = new ProtobufReader(body);
        ulong clientId = 0, steamId = 0;
        float interval = 0;
        byte[] requestId = Array.Empty<byte>();
        string? agreement = null, extendedError = null;
        bool weak = false;
        var confirmations = new List<AuthConfirmationType>();

        ProtoValue? v;
        while ((v = r.Next()) is not null)
        {
            switch (v.Value.FieldNumber)
            {
                case 1: clientId = v.Value.Varint; break;
                case 2: requestId = v.Value.Bytes; break;
                case 3: interval = AsFloat32(v.Value); break;
                case 4: confirmations.Add(ParseAllowedConfirmation(v.Value.Bytes)); break;
                case 5: steamId = v.Value.Varint; break;
                case 6: weak = v.Value.AsString().Length > 0 || v.Value.Varint != 0; break;
                case 7: agreement = v.Value.AsString(); break;
                case 8: extendedError = v.Value.AsString(); break;
            }
        }

        if (clientId == 0 && steamId == 0) return null;

        return new BeginAuthResult
        {
            ClientId = clientId,
            RequestId = requestId,
            Interval = interval,
            SteamId = steamId,
            Confirmations = confirmations,
            AgreementSessionUrl = agreement,
            HasWeakPassword = weak,
            ExtendedErrorMessage = extendedError,
        };
    }

    /// <summary>Steam encodes interval as a float in a fixed32 slot, not as an integer.</summary>
    private static float AsFloat32(ProtoValue v)
    {
        if (v.Kind == ProtoField.Fixed32 && v.Bytes is { Length: 4 })
            return BitConverter.ToSingle(v.Bytes, 0);
        return v.Varint;
    }

    private static AuthConfirmationType ParseAllowedConfirmation(byte[] body)
    {
        var r = new ProtobufReader(body);
        ProtoValue? v;
        while ((v = r.Next()) is not null)
        {
            // [1] confirmation_type enum
            if (v.Value.FieldNumber == 1)
                return (AuthConfirmationType)v.Value.AsInt();
        }
        return AuthConfirmationType.Invalid;
    }

    // ---- PollAuthSessionStatus ----
    //
    // request  [1] client_id varint · [2] request_id bytes
    public static string BuildPollRequest(ulong clientId, byte[] requestId)
    {
        var w = new ProtobufWriter().WriteVarint(1, clientId);
        if (requestId is { Length: > 0 }) w.WriteBytes(2, requestId);
        return w.ToBase64();
    }

    // [1] new_client_id            varint
    // [2] new_challenge_url        string
    // [3] refresh_token            string
    // [4] access_token             string
    // [5] had_remote_interaction   bool
    // [6] account_name             string
    // [7] new_guard_data           string
    public static PollAuthResult? ParsePollResponse(byte[] body)
    {
        var r = new ProtobufReader(body);
        ulong newClientId = 0;
        string? access = null, refresh = null, account = null, challenge = null, guard = null;
        bool remote = false;

        ProtoValue? v;
        while ((v = r.Next()) is not null)
        {
            switch (v.Value.FieldNumber)
            {
                case 1: newClientId = v.Value.Varint; break;
                case 2: challenge = v.Value.AsString(); break;
                case 3: refresh = v.Value.AsString(); break;
                case 4: access = v.Value.AsString(); break;
                case 5: remote = v.Value.AsBool(); break;
                case 6: account = v.Value.AsString(); break;
                case 7: guard = v.Value.AsString(); break;
            }
        }

        if (string.IsNullOrEmpty(access) && string.IsNullOrEmpty(refresh)) return null;

        return new PollAuthResult
        {
            NewClientId = newClientId,
            NewChallengeUrl = challenge,
            AccessToken = access,
            RefreshToken = refresh,
            AccountName = account,
            NewGuardData = guard,
            HadRemoteInteraction = remote,
        };
    }
}
