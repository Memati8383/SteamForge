using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamForge.Core.Engine;

/// <summary>
/// Resumable run state. Keyed on a fingerprint of the combo file (name, size, first and
/// last line) so resuming against a different file is refused rather than silently skipping
/// the wrong rows. Written atomically on a timer and on shutdown.
/// </summary>
public sealed class SessionState
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // Done is written from worker threads while the flush timer serializes it, so every
    // read/modify/snapshot of the set happens under this gate. Not serialized: it is a
    // readonly field and System.Text.Json only walks public properties.
    private readonly object _gate = new();

    public string Fingerprint { get; set; } = string.Empty;
    public int Processed { get; set; }
    public int Valid { get; set; }
    public int GuardLocked { get; set; }
    public int Invalid { get; set; }
    public int NotFound { get; set; }
    public int RateLimited { get; set; }
    public int Errors { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime LastWriteUtc { get; set; }

    /// <summary>
    /// Usernames already checked in this session, case-insensitively. Stored per account
    /// rather than as a count: workers finish out of order, so "the first N" is not a
    /// stable identity across a resume.
    /// </summary>
    public HashSet<string> Done { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int DoneCount { get { lock (_gate) { return Done.Count; } } }

    public bool IsDone(string username) { lock (_gate) { return Done.Contains(username); } }

    public void MarkDone(string username) { lock (_gate) { Done.Add(username); } }

    public static string ComputeFingerprint(string comboPath)
    {
        var info = new FileInfo(comboPath);
        if (!info.Exists) return string.Empty;

        string first = string.Empty, last = string.Empty;
        using (var reader = new StreamReader(comboPath))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Trim().Length == 0) continue;
                if (first.Length == 0) first = line.Trim();
                last = line.Trim();
            }
        }

        var raw = $"{info.Name}|{info.Length}|{first}|{last}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..24];
    }

    public static string StatePathFor(string outputDir, string fingerprint)
        => Path.Combine(outputDir, $"session_{fingerprint}.json");

    public static SessionState? TryLoad(string outputDir, string fingerprint)
    {
        var path = StatePathFor(outputDir, fingerprint);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<SessionState>(json, Opts);
            if (state is null || state.Fingerprint != fingerprint) return null;
            // JSON gives the set back with the default comparer; the run needs ignore-case.
            state.Done = new HashSet<string>(state.Done ?? new(), StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string outputDir)
    {
        string json;
        lock (_gate)
        {
            LastWriteUtc = DateTime.UtcNow;
            json = JsonSerializer.Serialize(this, Opts);
        }

        var path = StatePathFor(outputDir, Fingerprint);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Losing a resume point is annoying, never fatal.
        }
    }

    public void Delete(string outputDir)
    {
        try
        {
            var path = StatePathFor(outputDir, Fingerprint);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
