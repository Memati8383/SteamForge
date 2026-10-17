using System.Text;
using System.IO;
using System.Threading.Tasks;
using SteamForge.Core.Auth;
using SteamForge.Core.Combos;
using SteamForge.Core.Engine;
using SteamForge.Core.Proxy;

namespace SteamForge.Tests;

internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) Console.WriteLine($"  PASS  {name}");
        else { _fail++; Console.WriteLine($"  FAIL  {name}  {detail}"); }
    }

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("SteamForge core checks\n");

        TestComboParser();
        TestProxyParser();
        TestProtobufRoundTrip();
        TestRsa();
        TestProxyHealth();
        TestSessionState();

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : $"\n{_fail} FAILED");
        return _fail == 0 ? 0 : 1;
    }

    // ---- combo parsing: the messy real-world shapes ----

    private static void TestComboParser()
    {
        Console.WriteLine("[ComboParser]");

        var input = """
            # comment
            user1:Pass123
            someone@example.com:Hunter2!
            colonuser:aG9yYQ==:cGFzcw==
            cookieuser:SuperSecret:MOBILE:extra
            ReversedP@ss:reverseduser
            USER1:Pass123
            nocolonhere
            emptyuser:
            last:LastPass
            """;

        var report = ComboParser.Parse(input);
        var byUser = report.Combos.ToDictionary(c => c.Username, c => c.Password, StringComparer.OrdinalIgnoreCase);

        Check("plain user parsed", byUser.ContainsKey("user1"));
        Check("email account name parsed", byUser.ContainsKey("someone@example.com"));
        Check("trailing extra fields dropped",
            byUser.TryGetValue("cookieuser", out var cv) && cv == "SuperSecret",
            $"got '{cv}'");
        // Documented limitation: only fields 1 and 2 are read, so a colon inside the password
        // truncates. Trailing cookie/token fields are far more common than a colon in a
        // password, and the two are indistinguishable from text alone.
        Check("colon in password truncates at 2nd colon (documented)",
            byUser.TryGetValue("colonuser", out var cp) && cp == "aG9yYQ==",
            $"got '{(byUser.TryGetValue("colonuser", out var x) ? x : "<missing>")}'");
        Check("reversed line flipped to correct pair",
            byUser.ContainsKey("reverseduser") && byUser["reverseduser"] == "ReversedP@ss",
            $"users: {string.Join(",", byUser.Keys)}");
        Check("duplicate removed (case-insensitive)",
            report.DuplicatesRemoved >= 1 &&
            report.Combos.Count(c => c.Username.Equals("user1", StringComparison.OrdinalIgnoreCase)) == 1,
            $"dupes={report.DuplicatesRemoved}");
        Check("malformed no-colon skipped", report.SkippedMalformed >= 1, $"malformed={report.SkippedMalformed}");
        Check("comment counted", report.SkippedComment >= 1);
        Check("no empty-password rows", report.Combos.All(c => c.Password.Length > 0));

        Console.WriteLine($"        parsed={report.Combos.Count} dupes={report.DuplicatesRemoved} " +
                          $"malformed={report.SkippedMalformed} reversed={report.ReversedFixed}");

        TestTrailerFormat();
    }

    // ---- "user:pass | GamesList = [...]" — metadata trailer, may itself contain colons ----

    private static void TestTrailerFormat()
    {
        Console.WriteLine("\n[ComboParser.Trailer]");

        var input = """
            mironov20011:Wcwwgrhhehlach1 | GamesList = [War Thunder, Panzar]
            varmore:20dadavasya02 | GamesList = [Sid Meier's Civilization III: Complete, HITMAN™]
            gl1tchop:Vfbxtwhjujlwnf1 | GamesList = [Blender]
            plainuser:PlainPass1
            """;

        var report = ComboParser.Parse(input);
        var byUser = report.Combos.ToDictionary(c => c.Username, StringComparer.OrdinalIgnoreCase);

        Check("trailer stripped from password",
            byUser["mironov20011"].Password == "Wcwwgrhhehlach1",
            $"got '{byUser["mironov20011"].Password}'");
        Check("trailer captured as Note",
            byUser["mironov20011"].Note is not null && byUser["mironov20011"].Note!.Contains("War Thunder"),
            byUser["mironov20011"].Note);
        Check("colon inside trailer does not truncate password",
            byUser["varmore"].Password == "20dadavasya02",
            $"got '{byUser["varmore"].Password}'");
        Check("no-trailer row has Note=null",
            byUser["plainuser"].Password == "PlainPass1" && byUser["plainuser"].Note is null,
            byUser["plainuser"].Note);
        Check("all rows parsed, none malformed",
            report.SkippedMalformed == 0 && report.Combos.Count == 4,
            $"malformed={report.SkippedMalformed} count={report.Combos.Count}");

        // The regression that matters: a password must never carry trailer text. If it does,
        // every account in this file reads Invalid while the credentials are actually good.
        Check("no password contains GamesList annotation",
            report.Combos.All(c => !c.Password.Contains("GamesList", StringComparison.OrdinalIgnoreCase)));
    }

    // ---- proxy parsing: every format seen in the wild ----

    private static void TestProxyParser()
    {
        Console.WriteLine("\n[ProxyParser]");

        var lines = """
            1.2.3.4:8080
            5.6.7.8:1080:user:pass
            user:pass@9.9.9.9:9050
            http://2.3.4.5:8000
            socks5://3.4.5.6:1080
            socks5://suser:spass@4.5.6.7:1080
            socks4://7.7.7.7:9050
            not-a-proxy
            999.999.999.999:99999
            """;

        var list = ProxyPool.ParseLines(lines);

        var plain = list.FirstOrDefault(p => p.Host == "1.2.3.4");
        Check("bare host:port", plain is { Port: 8080, Scheme: ProxyScheme.Http });

        var auth4 = list.FirstOrDefault(p => p.Host == "5.6.7.8");
        Check("host:port:user:pass", auth4 is { Port: 1080, Username: "user", Password: "pass" },
            auth4 is null ? "missing" : $"{auth4.Username}:{auth4.Password}");

        var atForm = list.FirstOrDefault(p => p.Host == "9.9.9.9");
        Check("user:pass@host:port", atForm is { Username: "user", Password: "pass" });

        var scheme = list.FirstOrDefault(p => p.Host == "3.4.5.6");
        Check("socks5:// scheme detected", scheme is { Scheme: ProxyScheme.Socks5 });

        var schemeAuth = list.FirstOrDefault(p => p.Host == "4.5.6.7");
        Check("socks5 with auth", schemeAuth is { Scheme: ProxyScheme.Socks5, Username: "suser", Password: "spass" });

        var s4 = list.FirstOrDefault(p => p.Host == "7.7.7.7");
        Check("socks4:// scheme detected", s4 is { Scheme: ProxyScheme.Socks4 });

        Check("garbage line rejected", list.All(p => p.Host is not "not-a-proxy"));
        Check("out-of-range port rejected", list.All(p => p.Host is not "999.999.999.999"));

        var uri = schemeAuth?.ToUri();
        Check("ToUri escapes auth",
            uri is not null && uri.ToString().Contains("suser") && uri.Port == 1080,
            uri?.ToString());
    }

    // ---- protobuf wire format: what we emit must decode to what Steam expects ----

    private static void TestProtobufRoundTrip()
    {
        Console.WriteLine("\n[Protobuf]");

        // Request: write a BeginAuth-shaped message, decode it back field by field.
        var encoded = AuthMessages.BuildBeginAuthRequest(
            "myuser", "b64ciphertext==", "1700000000000000000",
            AuthPlatformType.MobileApp, "DeviceName", rememberLogin: true, persistence: AuthPersistence.Persistent);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (Exception ex) { Check("request encodes to base64", false, ex.Message); return; }
        Check("request is valid base64", true);

        var reader = new SteamForge.Core.Proto.ProtobufReader(bytes);
        string? acct = null, pw = null, site = null, device = null;
        ulong ts = 0; int platform = -1, persistence = -1; bool remember = false;

        SteamForge.Core.Proto.ProtoValue? v;
        while ((v = reader.Next()) is not null)
        {
            switch (v.Value.FieldNumber)
            {
                case 1: device = v.Value.AsString(); break;
                case 2: acct = v.Value.AsString(); break;
                case 3: pw = v.Value.AsString(); break;
                case 4: ts = v.Value.Varint; break;
                case 5: remember = v.Value.AsBool(); break;
                case 6: platform = v.Value.AsInt(); break;
                case 7: persistence = v.Value.AsInt(); break;
                case 8: site = v.Value.AsString(); break;
            }
        }

        // Field numbers below are Steam's own, read back from SteamKit2's serializer. The
        // previous test asserted the opposite layout (account_name in slot 1, fixed64 timestamp
        // in slot 3) and passed — which is how a wrong schema survived: the test agreed with it.
        Check("field 1 device_friendly_name", device == "DeviceName", device);
        Check("field 2 account_name", acct == "myuser", acct);
        Check("field 3 encrypted_password", pw == "b64ciphertext==", pw);
        Check("field 4 encryption_timestamp varint", ts == 1700000000000000000UL, ts.ToString());
        Check("field 5 remember_login", remember);
        // SteamKit2's generated EAuthTokenPlatformType: SteamClient=1, WebBrowser=2, MobileApp=3.
        // Asserting 2 here would have enshrined a WebBrowser claim as if it were MobileApp.
        Check("field 6 platform_type = MobileApp(3)", platform == 3, platform.ToString());
        Check("field 7 persistence = Persistent(1)", persistence == 1, persistence.ToString());
        Check("field 8 website_id", site == "Store", site);
        Check("no stray field 9", reader.First(9) is null);

        // Varint boundary: 0, 127, 128, 300 must survive.
        foreach (var n in new ulong[] { 0, 1, 127, 128, 300, 16384, ulong.MaxValue })
        {
            var w = new SteamForge.Core.Proto.ProtobufWriter().WriteVarint(9, n);
            var r = new SteamForge.Core.Proto.ProtobufReader(w.ToArray());
            var f = r.First(9);
            Check($"varint {n} round-trips", f is not null && f.Value.Varint == n,
                f is null ? "no field" : f.Value.Varint.ToString());
        }

        // Unknown fields must be skipped, not throw — Valve adds fields over time.
        var withUnknown = new SteamForge.Core.Proto.ProtobufWriter()
            .WriteString(1, "keepme")
            .WriteVarint(99, 12345)
            .WriteString(100, "future")
            .WriteString(77, "alsofuture")
            .ToArray();
        var ru = new SteamForge.Core.Proto.ProtobufReader(withUnknown);
        string? kept = null;
        SteamForge.Core.Proto.ProtoValue? uv;
        while ((uv = ru.Next()) is not null)
            if (uv.Value.FieldNumber == 1) kept = uv.Value.AsString();
        Check("unknown fields skipped without throwing", kept == "keepme", kept);

        // Parse a synthetic BeginAuth response. Wire types follow Steam: client_id and steamid
        // are varints, interval is a float in a fixed32 slot, and allowed_confirmations sits at
        // 4 (not 5) with steamid at 5.
        var respBytes = new SteamForge.Core.Proto.ProtobufWriter()
            .WriteVarint(1, 123456789UL)   // client_id
            .WriteBytes(2, Encoding.ASCII.GetBytes("reqid")) // request_id
            .WriteFloat32(3, 0.1f)          // interval
            .WriteMessage(4, m => m.WriteEnum(1, 3)) // allowed_confirmation: DeviceCode
            .WriteVarint(5, 76561198000000000UL) // steamid
            .ToArray();

        var begin = AuthMessages.ParseBeginAuthResponse(respBytes);
        Check("begin response: client_id", begin is { ClientId: 123456789UL },
            begin is null ? "null" : begin.ClientId.ToString());
        Check("begin response: steamid", begin is { SteamId: 76561198000000000UL },
            begin is null ? "null" : begin.SteamId.ToString());
        Check("begin response: request_id", begin is not null && begin.RequestId.Length == 5);
        Check("begin response: interval decoded as float", begin is { Interval: 0.1f },
            begin?.Interval.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");
        Check("begin response: confirmation parsed as DeviceCode",
            begin is { Confirmations: not null } && begin.Confirmations.Contains(SteamForge.Core.Auth.AuthConfirmationType.DeviceCode),
            begin is null ? "null" : string.Join(",", begin!.Confirmations!));

        // Poll response: refresh_token is field 3, access_token field 4 (not the reverse).
        var pollBytes = new SteamForge.Core.Proto.ProtobufWriter()
            .WriteVarint(1, 111UL)
            .WriteString(3, "refresh.token.value")
            .WriteString(4, "access.token.value")
            .ToArray();
        var poll = AuthMessages.ParsePollResponse(pollBytes);
        Check("poll response: access_token", poll is { AccessToken: "access.token.value" });
        Check("poll response: refresh_token", poll is { RefreshToken: "refresh.token.value" });

        // Poll with neither token must be null (not a success).
        var emptyPoll = new SteamForge.Core.Proto.ProtobufWriter().WriteVarint(1, 111UL).ToArray();
        Check("poll with no token returns null", AuthMessages.ParsePollResponse(emptyPoll) is null);

        // RSA key response — Steam sends timestamp as a varint, not a string.
        var rsaBytes = new SteamForge.Core.Proto.ProtobufWriter()
            .WriteString(1, "abcdef")
            .WriteString(2, "010001")
            .WriteVarint(3, 999)
            .ToArray();
        var rsa = AuthMessages.ParseRsaKeyResponse(rsaBytes);
        Check("rsa response: modulus", rsa is { ModulusHex: "abcdef" });
        Check("rsa response: varint timestamp decoded", rsa is { Timestamp: "999" }, rsa?.Timestamp);

        // ...and a string-encoded timestamp must also decode, since both have been observed.
        var rsaStr = new SteamForge.Core.Proto.ProtobufWriter()
            .WriteString(1, "abcdef").WriteString(2, "010001").WriteString(3, "999").ToArray();
        Check("rsa response: string timestamp also decoded",
            AuthMessages.ParseRsaKeyResponse(rsaStr) is { Timestamp: "999" });

        // Missing modulus must be null, not a half-built key.
        var badRsa = new SteamForge.Core.Proto.ProtobufWriter().WriteString(2, "010001").ToArray();
        Check("rsa response with no modulus is null", AuthMessages.ParseRsaKeyResponse(badRsa) is null);
    }

    // ---- RSA PKCS#1 v1.5 ----

    private static void TestRsa()
    {
        Console.WriteLine("\n[RSA]");

        // A 1024-bit test modulus. The point is padding structure, not Steam's real key.
        const string modHex = "c1a5f4a1c2f0f6b47d2c1e0f5e6a7b8c9d0e1f2031425364758697a8b9cadbec" +
                              "f00112233445566778899aabbccddeeff00112233445566778899aabbccddee" +
                              "ff00112233445566778899aabbccddeeff00112233445566778899aabbccddee" +
                              "ff00112233445566778899aabbccddeeff00112233445566778899aabbcc";
        const string expHex = "010001";

        // Encrypt twice: PKCS#1 v1.5 is non-deterministic (random padding), so the two
        // ciphertexts must differ while both decode to the right structure.
        string a, b;
        try
        {
            a = SteamRsa.EncryptPassword("hunter2", modHex, expHex);
            b = SteamRsa.EncryptPassword("hunter2", modHex, expHex);
        }
        catch (Exception ex)
        {
            Check("encrypt runs", false, ex.Message);
            return;
        }

        Check("encryption produces base64", !string.IsNullOrEmpty(a));
        Check("padding is randomized (two ciphertexts differ)", a != b);

        // Length must equal the modulus byte length (modulo base64 expansion).
        int keyBytes = (modHex.Length + 1) / 2;
        int expectedB64 = 4 * ((keyBytes + 2) / 3);
        Check($"ciphertext length == modulus length ({keyBytes} bytes)",
            Convert.FromBase64String(a).Length == keyBytes,
            $"got {Convert.FromBase64String(a).Length}, want {keyBytes}");

        // We cannot invert without the private key, so instead assert the PKCS#1 block
        // structure directly by decrypting with a synthetic private key would be ideal —
        // but structural checks on the input side still catch padding regressions:
        // a password longer than keyBytes-11 must throw.
        var tooLong = new string('x', keyBytes);
        try
        {
            SteamRsa.EncryptPassword(tooLong, modHex, expHex);
            Check("oversized password rejected", false, "no throw");
        }
        catch (ArgumentException)
        {
            Check("oversized password rejected", true);
        }

        // Different passwords must produce different ciphertexts (obviously, but it catches
        // a bug where the message block is built before padding).
        var c = SteamRsa.EncryptPassword("different", modHex, expHex);
        Check("distinct passwords produce distinct ciphertexts", c != a);

        // Unicode stripping: non-ASCII is dropped so the modulus length check never trips.
        var uni = SteamRsa.EncryptPassword("passwörd中文", modHex, expHex);
        Check("non-ASCII password handled without throw", !string.IsNullOrEmpty(uni));
    }

    // ---- proxy health bookkeeping ----

    private static void TestProxyHealth()
    {
        Console.WriteLine("\n[ProxyHealth]");

        var pool = new ProxyPool();
        pool.AddRange(ProxyPool.ParseLines("1.1.1.1:80\n2.2.2.2:80\n3.3.3.3:80\n4.4.4.4:80\n"));

        Check("pool count", pool.Count == 4);

        // Round-robin: four Next() calls must cover all four entries.
        var seen = new HashSet<string>();
        for (int i = 0; i < 4; i++)
        {
            var p = pool.Next();
            if (p is not null) seen.Add(p.Host);
        }
        Check("rotation covers the whole pool", seen.Count == 4, $"saw {seen.Count}");

        // A proxy with consecutive failures must enter cooldown and be skipped.
        var first = pool.Next()!;
        for (int i = 0; i < 4; i++)
            pool.MarkFailure(first, TimeSpan.FromMinutes(5));
        Check("repeated failure retires the proxy", first.IsDead);

        // Rotation must avoid the dead one entirely.
        bool hitDead = false;
        for (int i = 0; i < 40; i++)
            if (pool.Next()?.Host == first.Host) hitDead = true;
        Check("rotation skips dead proxies", !hitDead);

        var snap = pool.Snapshot();
        Check("snapshot counts dead", snap.Dead == 1 && snap.Alive == 3,
            $"alive={snap.Alive} dead={snap.Dead}");

        // Success resets consecutive fails and lifts an entry out of retirement.
        first.Revive();
        Check("revive clears dead flag", !first.IsDead && first.ConsecutiveFails == 0);

        // Success rate is lifetime (2 successes / 6 attempts). Revive() deliberately does NOT
        // reset the counters — resetting would let a chronically bad proxy erase its history
        // and look healthy after one lucky attempt. It only clears the dead flag and the
        // consecutive-failure streak so rotation will try it again.
        first.MarkSuccess(120);
        first.MarkSuccess(240);
        Check("success rate is lifetime, not reset by revive",
            Math.Abs(first.SuccessRate - (2.0 / 6.0)) < 0.001,
            first.SuccessRate.ToString("F2"));
        Check("revive clears consecutive streak", first.ConsecutiveFails == 0);
        Check("avg latency tracked", first.AvgLatencyMs is > 100 and < 250,
            first.AvgLatencyMs.ToString());

        // Pool must return null when empty rather than throwing.
        var empty = new ProxyPool();
        Check("empty pool returns null", empty.Next() is null);
    }

    // ---- session resume: a stopped run resumes, a finished one starts over ----

    private static void TestSessionState()
    {
        Console.WriteLine("[SessionState]");

        var dir = Path.Combine(Path.GetTempPath(), "sf_session_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string fp = "abc123def456";
            var s = new SessionState { Fingerprint = fp, StartedUtc = DateTime.UtcNow };

            Check("fresh session has nothing done", s.DoneCount == 0 && !s.IsDone("user1"));

            s.MarkDone("User1");
            s.MarkDone("user2");
            Check("done set is case-insensitive", s.IsDone("USER1") && s.IsDone("user2"));

            s.MarkDone("user1");
            Check("re-marking does not grow the set", s.DoneCount == 2, $"count={s.DoneCount}");

            s.Valid = 5;
            s.GuardLocked = 1;
            s.Processed = 2;
            s.Save(dir);

            var path = SessionState.StatePathFor(dir, fp);
            Check("session file written", File.Exists(path), path);
            Check("atomic write leaves no temp file", !File.Exists(path + ".tmp"));

            var back = SessionState.TryLoad(dir, fp);
            Check("session reloads", back is not null);
            Check("done set survives the round trip",
                back is not null && back.DoneCount == 2 && back.IsDone("user1") && back.IsDone("USER2"),
                back is null ? "null" : $"count={back.DoneCount}");
            Check("counters survive the round trip", back is { Valid: 5, GuardLocked: 1, Processed: 2 });
            Check("fingerprint mismatch refuses the file",
                SessionState.TryLoad(dir, "aDifferentFingerprint") is null);

            // Workers mark accounts from several threads while the flush timer serializes
            // the same set — an unsynchronized HashSet would corrupt or throw here.
            var parallel = new SessionState { Fingerprint = fp };
            Parallel.For(0, 4000, i => parallel.MarkDone($"user{i}"));
            Check("4000 parallel marks all land", parallel.DoneCount == 4000,
                $"count={parallel.DoneCount}");

            // Only a finished run clears it; a stopped one must keep its resume point.
            back!.Delete(dir);
            Check("delete clears the resume point",
                !File.Exists(path) && SessionState.TryLoad(dir, fp) is null);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* temp cleanup only */ }
        }
    }
}
