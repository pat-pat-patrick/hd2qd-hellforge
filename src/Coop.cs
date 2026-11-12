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
            if (perPlayer)
            {
                var text = ManifestText(m) ?? throw new HfError("no hellforge-mod.json: " + m.Name);
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

        // ---------------------------------------------------------------- every gameplay mod (the user, 2026-11-11)

        /// <summary>"Every gameplay mod only affects me": each code mod HellForge counts as shared
        /// gameplay (not one HellForge ships) is marked per-player, now and whenever one comes in,
        /// so the modlist code is HellForge's own mods only and players with different mods still
        /// match (the user, 2026-11-11: "把 gameplay mod 都声明成本地", for public matchmaking).
        /// HellForge refuses per-player for a mod whose changes every machine must share (network
        /// field types, a data spec: Mods.DecideTier in 0.4.0-beta): such a mod is put back as it
        /// was and remembered, by id and version, so it is not tried again until it changes.
        /// Kept in home\hellforge\coop-all.json.</summary>
        static string AllFile(string home) => Path.Combine(home, "hellforge", "coop-all.json");

        public static bool AllOn(string home) => Json.Bool(ReadAll(home), "on");

        /// <summary>The mods HellForge refused, "id|version" to its reason.</summary>
        public static Dictionary<string, string> Refused(string home)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Json.Obj(ReadAll(home), "refused") is Dictionary<string, object> r)
                foreach (var kv in r) d[kv.Key] = kv.Value as string;
            return d;
        }

        static Dictionary<string, object> ReadAll(string home)
        {
            try
            {
                var p = AllFile(home);
                return File.Exists(p) ? Json.Obj(File.ReadAllText(p, Encoding.UTF8)) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static void SaveAll(string home, bool on, Dictionary<string, string> refused, List<string> made) =>
            Placed.WriteText(AllFile(home), Json.Write(new Dictionary<string, object>
            {
                ["on"] = on, ["refused"] = refused.ToDictionary(kv => kv.Key, kv => (object)kv.Value), ["made"] = made.Cast<object>().ToList(),
            }));

        static List<string> Made(string home) => Json.List(ReadAll(home), "made").OfType<string>().ToList();

        static string Key(Hf.Mod m) => m.Id + "|" + m.Version;

        /// <summary>Why a mod's own switch cannot be changed while the setting is on: "all" (the
        /// setting decides: it would mark the mod again at the next read, so a switch of its own
        /// would only seem to work; the user, 2026-11-12: 锁定跟随总开关), "refused" (HellForge
        /// would not take it per-player), or null (its switch is free: the setting is off, or the
        /// mod is not one the setting covers).</summary>
        public static string Lock(bool allOn, IDictionary<string, string> refused, Hf.Mod m)
        {
            if (!allOn || !Applies(m) || (m.Group != "gameplay" && m.Group != "per-player")) return null;
            return refused.ContainsKey(Key(m)) ? "refused" : "all";
        }

        /// <summary>The setting turned on (every shared gameplay code mod marked now) or off (the
        /// marks it made taken out; a mod marked by hand is a choice of its own and is kept only
        /// when it was marked before the setting). Returns the names marked, and the refusals.</summary>
        public static (List<string> Marked, List<string> Refused) SetAll(string home, string install, bool on)
        {
            if (!on)
            {
                var made = Made(home);
                var list = Hf.ReadMods(install);
                foreach (var m in list.Mods.Where(x => made.Contains(x.Id, StringComparer.OrdinalIgnoreCase) && Applies(x)))
                    Set(home, install, m, false);
                SaveAll(home, false, new Dictionary<string, string>(), new List<string>());
                return (new List<string>(), new List<string>());
            }
            SaveAll(home, true, Refused(home), Made(home));
            return MarkAll(home, install);
        }

        /// <summary>With the setting on: each shared gameplay code mod not marked yet, marked.</summary>
        public static (List<string> Marked, List<string> Refused) MarkAll(string home, string install, Hf.ModList known = null)
        {
            var marked = new List<string>();
            var refusedNow = new List<string>();
            if (!AllOn(home)) return (marked, refusedNow);
            var refused = Refused(home);
            var made = Made(home);
            var list = known ?? Hf.ReadMods(install);              // the deploy's list when it has one
            foreach (var m in list.Mods.Where(x => x.Group == "gameplay" && Applies(x) && !refused.ContainsKey(Key(x))).ToList())
            {
                Set(home, install, m, true);
                var after = Hf.ReadMods(install);
                var now = after.Mods.FirstOrDefault(x => string.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase));
                var problem = after.Problems.FirstOrDefault(p => p.IndexOf(m.Name ?? "\0", StringComparison.OrdinalIgnoreCase) >= 0
                                                              || p.IndexOf(m.Id, StringComparison.OrdinalIgnoreCase) >= 0
                                                              || (m.Folder != null && p.IndexOf(Path.GetFileName(m.Folder), StringComparison.OrdinalIgnoreCase) >= 0)
                                                              || (m.Archive != null && p.IndexOf(Path.GetFileName(m.Archive), StringComparison.OrdinalIgnoreCase) >= 0));
                if (now == null || now.Group != "per-player" || problem != null)
                {
                    // HellForge would not take it per-player: as it was, and not tried again until it changes
                    Set(home, install, now ?? m, false);
                    refused[Key(m)] = m.Name + ": " + (problem ?? ("HellForge keeps it " + (now?.Group ?? "out")));
                    refusedNow.Add(refused[Key(m)]);
                    continue;
                }
                if (!made.Contains(m.Id, StringComparer.OrdinalIgnoreCase)) made.Add(m.Id);
                marked.Add(m.Name);
            }
            SaveAll(home, true, refused, made);
            return (marked, refusedNow);
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
