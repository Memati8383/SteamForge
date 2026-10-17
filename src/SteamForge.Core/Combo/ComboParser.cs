using System.Text;
using SteamForge.Core.Models;

namespace SteamForge.Core.Combos;

public sealed record ComboParseReport(
    IReadOnlyList<Core.Models.Combo> Combos,
    int TotalLines,
    int SkippedBlank,
    int SkippedComment,
    int SkippedMalformed,
    int DuplicatesRemoved,
    int ReversedFixed);

public static class ComboParser
{
    /// <summary>
    /// Parses a combo file into combos. Real combo lists are messy, so this handles the
    /// shapes that make naive parsers silently drop rows:
    ///   user:pass
    ///   user:pass:cookie:extra...     (trailing fields ignored, kept in SourceLine)
    ///   user:pass | GamesList = [...] (metadata trailer stripped into Note, not the password)
    ///   pass:user                     (reversed — decided by which side looks like an account)
    ///   # comment / blank lines
    /// Deliberate limitation: only the first two fields are read, so a password that itself
    /// contains ':' is cut at the second colon. Trailing cookie/token fields are far more
    /// common in real lists than a colon inside a password, and the two are not
    /// distinguishable from the text alone — guessing wrong silently yields a bad password.
    /// Duplicates are removed case-insensitively on username.
    /// </summary>
    public static ComboParseReport ParseFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Parse(reader.ReadToEnd());
    }

    public static ComboParseReport Parse(string content)
    {
        var combos = new List<Core.Models.Combo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int total = 0, blank = 0, comment = 0, malformed = 0, dupes = 0, reversed = 0;

        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r').Trim();
            total++;
            if (raw.Length == 0) { blank++; continue; }
            if (raw.StartsWith('#') || raw.StartsWith("//") || raw.StartsWith(";")) { comment++; continue; }

            // A metadata trailer (">>> GamesList = [...]") is annotation, not credential. It has
            // to come off before the credential split, or the whole trailer lands inside the
            // password and the account reads as invalid when it is not.
            var (credential, note) = StripTrailer(raw);

            if (!TrySplit(credential, out var user, out var pass, out var wasReversed))
            {
                malformed++;
                continue;
            }

            if (wasReversed) reversed++;

            if (!seen.Add(user))
            {
                dupes++;
                continue;
            }

            combos.Add(new Core.Models.Combo
            {
                Username = user,
                Password = pass,
                SourceLine = raw,
                LineNumber = i + 1,
                Note = note,
            });
        }

        return new ComboParseReport(combos, total, blank, comment, malformed, dupes, reversed);
    }

    /// <summary>
    /// Splits a trailing metadata annotation off the credential. Lists in the wild carry a
    /// note after a pipe, e.g. <c>user:pass | GamesList = [Dota 2, Half-Life: Alyx]</c> — and
    /// the note can contain colons, so it has to be removed before any colon-based split.
    /// </summary>
    private static (string Credential, string? Note) StripTrailer(string line)
    {
        int pipe = line.IndexOf(" | ", StringComparison.Ordinal);
        if (pipe < 0) return (line, null);

        var credential = line[..pipe].Trim();
        var note = line[(pipe + 3)..].Trim();
        return (credential, note.Length == 0 ? null : note);
    }

    private static bool TrySplit(string line, out string user, out string pass, out bool reversed)
    {
        user = string.Empty;
        pass = string.Empty;
        reversed = false;

        // Steam passwords may contain ':', account names never do. Split on the first colon.
        int idx = line.IndexOf(':');
        if (idx <= 0 || idx == line.Length - 1) return false;

        var left = line[..idx].Trim();
        var right = line[(idx + 1)..].Trim();

        // Trailing fields (cookie, totp, note) — keep only the first two.
        int secondColon = right.IndexOf(':');
        if (secondColon >= 0)
        {
            var maybePass = right[..secondColon].Trim();
            var maybeExtra = right[(secondColon + 1)..].Trim();

            if (LooksLikeEmail(maybePass) && !LooksLikeEmail(left))
            {
                // "pass:user@domain:extra" — the username is in field 2, the password is
                // field 1. Extra stays dropped; taking maybeExtra as the password would
                // silently submit the cookie instead of the password.
                (left, right) = (maybePass, left);
                reversed = true;
            }
            else
            {
                right = maybePass;
            }
        }
        else if (LooksLikeEmail(right) && !LooksLikeEmail(left))
        {
            (left, right) = (right, left);
            reversed = true;
        }
        else if (LooksLikeAccountName(right) && !LooksLikeAccountName(left) && LooksLikeSecret(left))
        {
            (left, right) = (right, left);
            reversed = true;
        }

        if (left.Length == 0 || right.Length == 0) return false;
        user = left;
        pass = right;
        return true;
    }

    /// <summary>
    /// A real email: something@domain.tld. Merely containing '@' is not enough — that would
    /// misread a password like <c>Pass@ssw0rd</c> as an account name and so never notice the
    /// line is reversed.
    /// </summary>
    private static bool LooksLikeEmail(string s)
    {
        int at = s.IndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;
        int dot = s.IndexOf('.', at + 1);
        return dot > at + 1 && dot < s.Length - 1;
    }

    /// <summary>Steam account names are typically an email, a persona handle, or a SteamID64 — never a random secret.</summary>
    private static bool LooksLikeAccountName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (LooksLikeEmail(s)) return true;
        if (ulong.TryParse(s, out var sid) && sid >= 76561197960265728UL) return true;
        // Persona-style handle: letters/digits/underscore/dash, reasonably long.
        if (s.Length >= 3 && s.Length <= 64 && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
            return true;
        return false;
    }

    private static bool LooksLikeSecret(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Length >= 4 && !LooksLikeEmail(s);
    }
}
