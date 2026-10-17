using System.Reflection;
using SteamKit2.Internal;
using SteamForge.Core.Proto;

namespace SteamForge.Probe;

/// <summary>
/// Prints SteamKit2's actual wire map for every message this checker touches.
///
/// Reflection on the protobuf attributes did not expose the tags, but serializing a real
/// instance does — and the wire map is what Steam actually parses, so it is the ground truth
/// the hand-rolled writer has to match. Every field number below came from Steam's own
/// serializer, not from memory or documentation.
/// </summary>
internal static class Program9
{
    public static void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Dump("GetPasswordRSAPublicKey_Request", new CAuthentication_GetPasswordRSAPublicKey_Request
        {
            account_name = "someuser",
        });

        Dump("GetPasswordRSAPublicKey_Response", new CAuthentication_GetPasswordRSAPublicKey_Response
        {
            publickey_mod = "aabb",
            publickey_exp = "010001",
            timestamp = 184926350000UL,
        });

        Dump("BeginAuthSessionViaCredentials_Request", new CAuthentication_BeginAuthSessionViaCredentials_Request
        {
            device_friendly_name = "DEVICE",
            account_name = "someuser",
            encrypted_password = "ENCRYPTED",
            encryption_timestamp = 184926350000UL,
            remember_login = true,
            platform_type = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
            persistence = ESessionPersistence.k_ESessionPersistence_Persistent,
            website_id = "Store",
        });

        Dump("BeginAuthSessionViaCredentials_Response", new CAuthentication_BeginAuthSessionViaCredentials_Response
        {
            client_id = 4984930505053217245UL,
            request_id = new byte[] { 1, 2, 3, 4, 5 },
            interval = 0.1f,
            steamid = 76561198000000000UL,
        });

        Dump("PollAuthSessionStatus_Request", new CAuthentication_PollAuthSessionStatus_Request
        {
            client_id = 4984930505053217245UL,
            request_id = new byte[] { 1, 2, 3, 4, 5 },
        });

        Dump("PollAuthSessionStatus_Response", new CAuthentication_PollAuthSessionStatus_Response
        {
            new_client_id = 111UL,
            new_challenge_url = "URL",
            refresh_token = "REFRESH",
            access_token = "ACCESS",
            had_remote_interaction = true,
            account_name = "someuser",
            new_guard_data = "GUARD",
        });

        Console.WriteLine();
        Console.WriteLine("allowed_confirmation element (CAuthentication_AllowedConfirmation):");
        var ac = typeof(SteamKit2.SteamClient).Assembly.GetTypes()
            .First(x => x.Name == "CAuthentication_AllowedConfirmation");
        var inst = Activator.CreateInstance(ac)!;
        foreach (var p in ac.GetProperties())
        {
            if (p.PropertyType.IsEnum) p.SetValue(inst, Enum.GetValues(p.PropertyType).GetValue(1));
            else if (p.PropertyType == typeof(uint)) p.SetValue(inst, 3u);
            else if (p.PropertyType == typeof(int)) p.SetValue(inst, 3);
            else p.SetValue(inst, "X");
        }
        DumpInner(ac, inst);
    }

    private static void Dump<T>(string label, T msg) where T : class
    {
        Console.WriteLine($"=== {label} ===");
        DumpInner(typeof(T), msg);
    }

    private static void DumpInner(Type t, object msg)
    {
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, msg);
        var bytes = ms.ToArray();

        var r = new ProtobufReader(bytes);
        ProtoValue? v;
        while ((v = r.Next()) is not null)
        {
            var val = v.Value.Kind switch
            {
                ProtoField.Varint => v.Value.Varint.ToString(),
                ProtoField.Fixed64 => v.Value.AsFixed64().ToString(),
                ProtoField.Fixed32 => BitConverter.ToUInt32(v.Value.Bytes, 0).ToString(),
                _ => v.Value.AsString().Length > 30 ? v.Value.AsString()[..30] + "…" : v.Value.AsString(),
            };
            Console.WriteLine($"  [{v.Value.FieldNumber,2}] {v.Value.Kind,-16} {val}");
        }
        Console.WriteLine();
    }
}
