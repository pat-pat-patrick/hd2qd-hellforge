using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Qd.HellForge
{
    /// <summary>"Only affects me": a code mod's manifest made to say "coop": "per-player", so
    /// HellForge counts it as each player's own (not in the modlist code friends compare, never
    /// downloaded in co-op; docs mods.md). For packs that were "whoever installs it uses it" on the
    /// official game, such as Vanilla Plus Megapack, whose manifest says nothing and which
    /// HellForge therefore compared (2026-11-11).
    ///
    /// The change is written into the copy HellForge holds (its zip, or its folder; a hard link to
    /// the library is broken first, so the library's file stays as it was), never into the library.
    /// The player's choice is kept in home\hellforge\coop.json and made again when a new version
    /// of the mod comes without it. The original zip or manifest is kept in home\hellforge\backups.</summary>
    static class Coop
    {
        static string ChoiceFile(string home) => Path.Combine(home, "hellforge", "coop.json");

        /// <summary>The mods the player marked, by HellForge id, each with the coop value its own
        /// manifest had (null: none).</summary>
        public static Dictionary<string, string> Choices(string home)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var p = ChoiceFile(home);
                if (File.Exists(p) && Json.Obj(File.ReadAllText(p, Encoding.UTF8)) is Dictionary<string, object> o)
                    foreach (var kv in o) d[kv.Key] = (kv.Value as Dictionary<string, object>) is Dictionary<string, object> e ? Json.Str(e, "original") : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return d;
        }

        static void SaveChoices(string home, Dictionary<string, string> d) =>
            Placed.WriteText(ChoiceFile(home), Json.Write(d.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object> { ["original"] = kv.Value })));

        /// <summary>Whether the switch applies: a code mod that is not HellForge's own.</summary>
        public static bool Applies(Hf.Mod m) => m.Has("code") && !m.Has("bundled") && !m.Has("local-stats");

        static readonly Regex CoopValue = new Regex("\"coop\"\\s*:\\s*\"([^\"]*)\"");

        /// <summary>The coop value a manifest's text says, or null.</summary>
        public static string ValueIn(string manifestText)
        {
            var m = CoopValue.Match(manifestText ?? "");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>The manifest's text with "coop" set to value, or taken out (value null). The rest
        /// of the text stays byte for byte; null when the result would not be a JSON object.</summary>
        public static string With(string text, string value)
        {
            string result;
            var m = CoopValue.Match(text);
            if (value == null)
            {
                result = text;
                if (m.Success)
                {
                    // "coop": "..." with the comma after it, or (the last key) the comma before it
                    var after = new Regex("\"coop\"\\s*:\\s*\"[^\"]*\"\\s*,[ \\t]*(\\r?\\n[ \\t]*)?");
                    var before = new Regex(",\\s*\"coop\"\\s*:\\s*\"[^\"]*\"");
                    result = after.IsMatch(text) ? after.Replace(text, "", 1) : before.Replace(text, "", 1);
                }
            }
            else if (m.Success)
                result = text.Substring(0, m.Groups[1].Index) + value + text.Substring(m.Groups[1].Index + m.Groups[1].Length);
            else
            {
                int brace = text.IndexOf('{');
                if (brace < 0) return null;
                int nl = text.IndexOf('\n', brace);
                var indent = "  ";
                if (nl > 0)
                {
                    var next = text.Substring(nl + 1);
                    indent = new string(next.TakeWhile(c => c == ' ' || c == '\t').ToArray());
                    if (indent.Length == 0) indent = "  ";
                }
                var eol = text.Contains("\r\n") ? "\r\n" : "\n";
                // a line of its own after the brace; the line that followed keeps its indent
                result = text.Substring(0, brace + 1) + eol + indent + "\"coop\": \"" + value + "\"," + text.Substring(brace + 1);
            }
            return Json.Obj(result) != null && ValueIn(result) == value ? result : null;
        }

        /// <summary>The text of the manifest HellForge reads for the mod (its folder, which for a zip
        /// is HellForge's unpacked copy).</summary>
        public static string ManifestText(Hf.Mod m)
        {
            var p = m.Folder != null ? Placed.CodeManifest(m.Folder) : null;
            return p != null ? File.ReadAllText(p, Encoding.UTF8) : null;
        }

        public static bool IsPerPlayer(Hf.Mod m)
        {
            try { return ValueIn(ManifestText(m)) == "per-player"; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>The switch turned on or off for a mod: its copy in HellForge changed now, and the
        /// choice kept for later versions.</summary>
        public static void Set(string home, string install, Hf.Mod m, bool perPlayer)
        {
            if (!Applies(m)) throw new HfError("not a code mod: " + m.Id);
            var choices = Choices(home);
            var text = ManifestText(m) ?? throw new HfError("no hellforge-mod.json: " + m.Name);
            if (perPlayer)
            {
                if (!choices.ContainsKey(m.Id)) choices[m.Id] = ValueIn(text);
                Write(home, install, m, "per-player");
            }
            else
            {
                choices.TryGetValue(m.Id, out var original);
                Write(home, install, m, original == "per-player" ? null : original);
                choices.Remove(m.Id);
            }
            SaveChoices(home, choices);
        }

        /// <summary>The marked mods whose copy in HellForge lost the value (a new version came)
        /// made per-player again. Returns their names.</summary>
        public static List<string> Reapply(string home, string install, Hf.ModList list)
        {
            var done = new List<string>();
            var choices = Choices(home);
            foreach (var m in list.Mods.Where(x => choices.ContainsKey(x.Id) && Applies(x)))
            {
                string text;
                try { text = ManifestText(m); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { continue; }
                if (text == null || ValueIn(text) == "per-player") continue;
                Write(home, install, m, "per-player");
                done.Add(m.Name);
            }
            return done;
        }

        /// <summary>The value written where HellForge keeps the mod: inside its zip (HellForge
        /// unpacks it again when the zip changes), or in its folder's manifest.</summary>
        static void Write(string home, string install, Hf.Mod m, string value)
        {
            var mods = Path.GetFullPath(Install.Mods(install)).TrimEnd('\\');
            if (!string.IsNullOrEmpty(m.Archive))
            {
                var zip = Path.IsPathRooted(m.Archive) ? m.Archive : Path.Combine(mods, m.Archive);
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(zip)), mods, StringComparison.OrdinalIgnoreCase))
                    throw new HfError("not a zip of mods\\: " + zip);
                Backup(home, zip);
                var tmp = zip + ".hd2qd.tmp";
                File.Copy(zip, tmp, true);
                try
                {
                    using (var a = ZipFile.Open(tmp, ZipArchiveMode.Update))
                    {
                        var entry = a.Entries.Where(e => e.Name.Equals("hellforge-mod.json", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("bts-mod.json", StringComparison.OrdinalIgnoreCase))
                                            .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault() ?? throw new HfError("no hellforge-mod.json in " + zip);
                        string text;
                        using (var r = new StreamReader(entry.Open(), Encoding.UTF8)) text = r.ReadToEnd();
                        var changed = With(text, value) ?? throw new HfError("could not change " + entry.FullName);
                        var name = entry.FullName;
                        entry.Delete();
                        using (var w = new StreamWriter(a.CreateEntry(name).Open(), new UTF8Encoding(false))) w.Write(changed);
                    }
                    File.Copy(tmp, zip, true);
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
                return;
            }
            var manifest = Placed.CodeManifest(m.Folder) ?? throw new HfError("no hellforge-mod.json: " + m.Name);
            if (!string.Equals(Path.GetFullPath(manifest).Substring(0, Math.Min(mods.Length, Path.GetFullPath(manifest).Length)), mods, StringComparison.OrdinalIgnoreCase))
                throw new HfError("not a mod of mods\\: " + manifest);
            Backup(home, manifest);
            var now = File.ReadAllText(manifest, Encoding.UTF8);
            var next = With(now, value) ?? throw new HfError("could not change " + manifest);
            // delete, then write: a hard link to the library's file is broken, never written through
            var tmpFile = manifest + ".hd2qd.tmp";
            File.WriteAllText(tmpFile, next, new UTF8Encoding(false));
            File.Delete(manifest);
            File.Move(tmpFile, manifest);
        }

        /// <summary>The file as it was, once per content, in home\hellforge\backups.</summary>
        static void Backup(string home, string file)
        {
            string hash;
            using (var s = File.OpenRead(file))
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            var dir = Path.Combine(home, "hellforge", "backups");
            var to = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "-" + hash + Path.GetExtension(file));
            if (File.Exists(to)) return;
            Directory.CreateDirectory(dir);
            File.Copy(file, to);
        }
    }
}
