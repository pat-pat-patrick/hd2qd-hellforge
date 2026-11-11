using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Qd.HellForge
{
    /// <summary>HellForge as a deploy target of HD2 Mod Quick Deploy (the manager's Targets and
    /// PluginTargets; the user, 2026-11-11: "我需要在管理器能正常管理 HellForge 的 MOD"). The
    /// manager keeps HellForge's own list of mods, order and options; a deploy hands this plugin
    /// that list as host["mods"]: each enabled mod with every file a deploy puts in the game, its
    /// options and sub-options already chosen, named as the game folder would have them.
    ///
    /// Each mod becomes a folder of its own in HellForge's mods\ (a pack with its patches at its
    /// top and a manifest.json of its own Guid, made from the library id), its files hard links to
    /// the library's (copies on another drive), made ready for HellForge's game by Port (text
    /// tables merged, render configuration, shader libraries and replaced game scripts left out).
    /// Then the order (the manager's last mod wins; HellForge's top one does: the order is turned
    /// round), each one switched on, and the folders of mods no longer on taken out. A mod that
    /// did not change since the last deploy is left as it is.
    ///
    /// Only folders recorded in home\hellforge\deployed.json are this target's: a mod HellForge
    /// has from elsewhere (bundled, added in HellForge.exe, a port put there by hand) is never
    /// touched; a library mod whose own Guid HellForge already has that way is not deployed, and
    /// said.</summary>
    public static partial class Plugin
    {
        const string TargetId = "hellforge";

        static string Lang(Dictionary<string, object> host) => Words.Lang(host);

        public static Dictionary<string, object> Target(Dictionary<string, object> host)
        {
            var home = Home(host);
            var install = Found(home);
            return new Dictionary<string, object>
            {
                ["id"] = TargetId, ["title"] = "HellForge", ["found"] = install != null,
                ["why"] = install == null ? Words.Get(Lang(host), "target_not_found") : null,
            };
        }

        public static Dictionary<string, object> TargetStatus(Dictionary<string, object> host)
        {
            var home = Home(host);
            var install = Found(home);
            if (install == null) return new Dictionary<string, object> { ["said"] = Words.Get(Lang(host), "target_not_found") };
            bool running = false;
            try { running = Hf.ReadStatus(install)?.Running == true; } catch (Exception e) when (e is HfError || e is IOException) { }
            int pending = 0;
            try { pending = Deployed.Pending(home, install, Data(host), Mods(host)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return new Dictionary<string, object> { ["pending"] = (long)pending, ["running"] = running };
        }

        public static Dictionary<string, object> TargetDeploy(Dictionary<string, object> host)
        {
            var home = Home(host);
            var lang = Lang(host);
            var install = Found(home, true) ?? throw new HfError(Words.Get(lang, "target_not_found"));
            var s = Hf.ReadStatus(install);
            if (s.Running) throw new HfError(Words.Get(lang, "locked"));
            if (!s.GameCopy) throw new HfError(Words.Get(lang, "not_ready"));
            var r = Deployed.Run(home, install, Data(host), Mods(host), lang);
            try
            {
                var list = Hf.ReadMods(install);
                Coop.Reapply(home, install, list);
                foreach (var refused in Coop.MarkAll(home, install).Refused) r.Refused.Add(refused);
            }
            catch (Exception e) when (e is HfError || e is IOException || e is UnauthorizedAccessException) { r.Refused.Add(e.Message); }
            notesAt = DateTime.MinValue;
            return new Dictionary<string, object>
            {
                ["said"] = Words.F(lang, "target_said", r.Mods, r.Placed, r.Kept, r.Removed),
                ["refused"] = r.Refused.Cast<object>().ToList(),
            };
        }

        public static string TargetPurge(Dictionary<string, object> host)
        {
            var home = Home(host);
            var install = Found(home, true) ?? throw new HfError(Words.Get(Lang(host), "target_not_found"));
            if (Hf.ReadStatus(install).Running) throw new HfError(Words.Get(Lang(host), "locked"));
            int n = Deployed.Purge(home, install);
            notesAt = DateTime.MinValue;
            return Words.F(Lang(host), "target_purged", n);
        }

        /// <summary>A folder that is HellForge's own kind of mod (hellforge-mod.json, or bts-mod.json
        /// from before the rename): added to the manager's library, it is for HellForge only.</summary>
        public static bool TargetClaims(string folder, Dictionary<string, object> host) =>
            !string.IsNullOrEmpty(folder) && Directory.Exists(folder) && Placed.CodeManifest(folder) != null;

        /// <summary>The window opened and a session started, as its Play button does.</summary>
        public static void TargetLaunch(System.Windows.Window owner, Dictionary<string, object> host)
        {
            Open(owner, host);
            if (open?.Tag is View v) v.PlayWhenRead();
        }

        /// <summary>The list in use, as the manager hands it: each enabled mod {id, name, path, files [{src, name}]}.</summary>
        internal static List<Deployed.Want> Mods(Dictionary<string, object> host)
        {
            if (!(host.TryGetValue("mods", out var f) && f is Func<List<Dictionary<string, object>>> mods)) return new List<Deployed.Want>();
            return mods().Select(m => new Deployed.Want
            {
                Id = m.TryGetValue("id", out var id) ? id as string : null,
                Name = m.TryGetValue("name", out var n) ? n as string : null,
                Path = m.TryGetValue("path", out var p) ? p as string : null,
                For = m.TryGetValue("for", out var fo) ? fo as string : null,
                Files = (m.TryGetValue("files", out var fl) && fl is IEnumerable<object> files ? files : new object[0])
                        .OfType<Dictionary<string, object>>().Select(x => (Src: x["src"] as string, Name: x["name"] as string))
                        .Where(x => x.Src != null && x.Name != null).ToList(),
            }).Where(w => w.Id != null && (w.Files.Count > 0 || (w.Path != null && Directory.Exists(w.Path) && Placed.CodeManifest(w.Path) != null))).ToList();
        }
    }

    /// <summary>What the deploy target put into HellForge, and the deploy itself.</summary>
    static class Deployed
    {
        public sealed class Want
        {
            public string Id, Name, Path;
            /// <summary>The target the library says it is made for (null: both): a build made for
            /// HellForge (a port) goes in as it is, never made ready again.</summary>
            public string For;
            public bool MadeForHellForge => For == "hellforge";
            public List<(string Src, string Name)> Files = new List<(string, string)>();
            /// <summary>HellForge's own kind of mod (hellforge-mod.json): its whole folder goes in as it is,
            /// HellForge builds and loads it; its id is the manifest's.</summary>
            public bool Whole => Path != null && Placed.CodeManifest(Path) != null;

            /// <summary>Every file of a whole mod's folder, relative, else the planned files.</summary>
            public List<(string Src, string Name)> AllFiles() =>
                !Whole ? Files.Concat(TopFiles()).ToList()
                : Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
                             .Where(f => !f.Substring(Path.TrimEnd('\\').Length + 1).StartsWith(".git"))
                             .Select(f => (f, f.Substring(Path.TrimEnd('\\').Length + 1))).OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase).ToList();

            /// <summary>A pack's own files at its top, beside its patches (not those, not its manifest.json).</summary>
            IEnumerable<(string Src, string Name)> TopFiles() =>
                Path == null || !Directory.Exists(Path) ? Enumerable.Empty<(string, string)>()
                : Directory.EnumerateFiles(Path).Select(f => (f, System.IO.Path.GetFileName(f)))
                           .Where(x => !Placed.IsPatch(x.Item2) && !string.Equals(x.Item2, "manifest.json", StringComparison.OrdinalIgnoreCase))
                           .OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase);
        }

        public sealed class Entry
        {
            public string QdId, Folder, PackId, Name, Signature, Level, Games, Library;
            public int Merged;
            public Dictionary<string, int> LeftOutTypes = new Dictionary<string, int>(), KeptDiffering = new Dictionary<string, int>();
            public List<string> Notes = new List<string>();
        }

        public sealed class Result
        {
            public int Mods, Placed, Kept, Removed;
            public List<string> Refused = new List<string>();
        }

        static string RecordFile(string home) => System.IO.Path.Combine(home, "hellforge", "deployed.json");
        static string Key(string install) => System.IO.Path.GetFullPath(install).TrimEnd('\\').ToLowerInvariant();

        public static Dictionary<string, Entry> Load(string home, string install)
        {
            var mine = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> all;
            try { all = File.Exists(RecordFile(home)) ? Json.Obj(File.ReadAllText(RecordFile(home), Encoding.UTF8)) : null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { all = null; }
            foreach (var kv in Json.Obj(all, Key(install)) ?? new Dictionary<string, object>())
                if (kv.Value is Dictionary<string, object> e)
                {
                    var entry = new Entry
                    {
                        QdId = kv.Key, Folder = Json.Str(e, "folder"), PackId = Json.Str(e, "pack"), Name = Json.Str(e, "name"),
                        Signature = Json.Str(e, "signature"), Level = Json.Str(e, "level"), Games = Json.Str(e, "games"),
                        Merged = (int)Json.Long(e, "merged"), Notes = Json.Strs(e, "notes"), Library = Json.Str(e, "library"),
                    };
                    foreach (var t in Json.Obj(e, "left_out_types") ?? new Dictionary<string, object>()) entry.LeftOutTypes[t.Key] = (int)Json.Long(Json.Obj(e, "left_out_types"), t.Key);
                    foreach (var t in Json.Obj(e, "kept_differing") ?? new Dictionary<string, object>()) entry.KeptDiffering[t.Key] = (int)Json.Long(Json.Obj(e, "kept_differing"), t.Key);
                    mine[kv.Key] = entry;
                }
            return mine;
        }

        static void Save(string home, string install, Dictionary<string, Entry> mine)
        {
            Dictionary<string, object> all = null;
            try { all = File.Exists(RecordFile(home)) ? Json.Obj(File.ReadAllText(RecordFile(home), Encoding.UTF8)) : null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            all = all ?? new Dictionary<string, object>();
            all[Key(install)] = mine.Values.ToDictionary(e => e.QdId, e => (object)new Dictionary<string, object>
            {
                ["folder"] = e.Folder, ["pack"] = e.PackId, ["name"] = e.Name, ["signature"] = e.Signature, ["level"] = e.Level, ["games"] = e.Games,
                ["merged"] = e.Merged, ["notes"] = e.Notes.Cast<object>().ToList(), ["library"] = e.Library,
                ["left_out_types"] = e.LeftOutTypes.ToDictionary(k => k.Key, k => (object)k.Value),
                ["kept_differing"] = e.KeptDiffering.ToDictionary(k => k.Key, k => (object)k.Value),
            });
            Placed.WriteText(RecordFile(home), Json.Write(all));
        }

        /// <summary>The pack's Guid for a library id: the same every time.</summary>
        public static string GuidOf(string qdId)
        {
            using (var md5 = MD5.Create())
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("hd2qd:" + qdId))).ToString("D");
        }

        static string HfData(string install) => System.IO.Path.Combine(install, "game", "data");

        /// <summary>What a mod's copy in HellForge is made from: its name, each file (source, name,
        /// size, time) and the two games. A copy with the same signature needs nothing.</summary>
        public static string Signature(Want w, string games)
        {
            var sb = new StringBuilder(w.Name).Append('\n').Append(games ?? "").Append('\n')
                .Append(w.MadeForHellForge ? "as it is" : "made ready").Append('\n');       // marked for HellForge or not: placed again
            foreach (var (src, name) in w.AllFiles())
            {
                var fi = new FileInfo(src);
                sb.Append(name).Append('|').Append(src).Append('|').Append(fi.Exists ? fi.Length + "|" + fi.LastWriteTimeUtc.Ticks : "missing").Append('\n');
            }
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        }

        static string Games(string officialData, string install) =>
            officialData != null && File.Exists(System.IO.Path.Combine(officialData, "bundles.nxa")) ? Placed.Games(officialData, HfData(install)) : "none";

        /// <summary>How many mods a deploy would place, place again or take out.</summary>
        public static int Pending(string home, string install, string officialData, List<Want> wants)
        {
            var mine = Load(home, install);
            var games = Games(officialData, install);
            int n = wants.Count(w => !mine.TryGetValue(w.Id, out var e) || e.Signature != Signature(w, games)
                                     || (e.Folder != null && !Directory.Exists(System.IO.Path.Combine(Install.Mods(install), e.Folder))));
            n += mine.Keys.Count(id => wants.All(w => !string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)));
            return n;
        }

        public static Result Run(string home, string install, string officialData, List<Want> wants, string lang)
        {
            var res = new Result();
            var mods = Install.Mods(install);
            var mine = Load(home, install);
            var games = Games(officialData, install);
            var list = Hf.ReadMods(install);
            var placedByHand = Placed.Load(home, install);                 // the plugin window's "add from the library", before this target
            var ours = new HashSet<string>(mine.Values.Select(e => e.PackId).Where(x => x != null), StringComparer.OrdinalIgnoreCase);

            // what changed: made again; everything else kept
            var toPlace = new List<Want>();
            foreach (var w in wants)
            {
                // a library mod HellForge already has from elsewhere (its own Guid, or a HellForge mod's id): left to the player
                var ownId = w.Whole ? Placed.IdOf(w.Path, System.IO.Path.GetFileName(w.Path)) : OwnGuid(w.Path) is string g ? "pack." + g : null;
                var clash = ownId == null ? null : list.Mods.FirstOrDefault(m => string.Equals(m.Id, ownId, StringComparison.OrdinalIgnoreCase)
                                                                                && !ours.Contains(m.Id) && placedByHand.Values.All(p => !string.Equals(p.Id, m.Id, StringComparison.OrdinalIgnoreCase)));
                if (clash != null) { res.Refused.Add(Words.F(lang, "target_clash", w.Name, clash.Name)); continue; }
                // a copy the plugin window put there by hand gives way to the deploy
                foreach (var p in placedByHand.Values.Where(p => Placed.SamePath(p.Library, w.Path)).ToList())
                    try { Placed.Remove(home, install, p); } catch (HfError e) { res.Refused.Add(w.Name + ": " + e.Message); }
                res.Mods++;
                if (mine.TryGetValue(w.Id, out var e2) && e2.Signature == Signature(w, games) && (e2.Folder == null || Directory.Exists(System.IO.Path.Combine(mods, e2.Folder))))
                { res.Kept++; continue; }
                toPlace.Add(w);
            }
            var hfData = HfData(install);
            var reports = toPlace.Count > 0 && games != "none"
                ? Port.CheckSets(home, officialData, hfData, toPlace.Where(w => !w.Whole && !w.MadeForHellForge).ToDictionary(w => w.Id, w => w.Files.Select(f => (f.Src, f.Name)).ToList(), StringComparer.OrdinalIgnoreCase))
                : new Dictionary<string, Port.Report>();
            foreach (var w in toPlace)
            {
                try
                {
                    mine.TryGetValue(w.Id, out var old);
                    var folder = old?.Folder ?? FreeName(mods, Placed.FolderName(w.Name));
                    var entry = Place(install, w, folder, reports.TryGetValue(w.Id, out var rep) ? rep : null, officialData, hfData);
                    entry.Signature = Signature(w, games);
                    entry.Games = games;
                    mine[w.Id] = entry;
                    if (entry.Folder == null) { res.Refused.Add(Words.F(lang, "target_nothing_left", w.Name)); continue; }
                    ours.Add(entry.PackId);
                    res.Placed++;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
                {
                    res.Refused.Add(w.Name + ": " + e.Message);
                }
            }
            Save(home, install, mine);

            // mods no longer on: their folders taken out (HellForge's own mod-remove)
            foreach (var gone in mine.Values.Where(e => wants.All(w => !string.Equals(w.Id, e.QdId, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                var folder = System.IO.Path.Combine(mods, gone.Folder ?? "\0");
                if (Directory.Exists(folder))
                {
                    var r = Hf.Run(install, "mod-remove", "--id", gone.PackId);
                    // one HellForge does not list (it could not read it: 2026-11-11, an empty pack) is ours to delete
                    if (!r.Ok && Directory.Exists(folder))
                        try { Directory.Delete(folder, true); }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { res.Refused.Add(gone.Name + ": " + r.Why + "; " + e.Message); continue; }
                }
                mine.Remove(gone.QdId);
                res.Removed++;
            }
            Save(home, install, mine);

            // the order (the manager's last wins; HellForge's top one does) and every one on
            list = Hf.ReadMods(install);
            var order = wants.Where(w => mine.ContainsKey(w.Id)).Select(w => mine[w.Id].PackId).Reverse().ToList();
            var present = new HashSet<string>(list.Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            order = order.Where(present.Contains).ToList();
            if (order.Count > 0)
            {
                var r = Hf.Run(install, "mod-order", "--ids", string.Join(",", order));
                if (!r.Ok) res.Refused.Add("mod-order: " + r.Why);
            }
            foreach (var m in list.Mods.Where(m => order.Contains(m.Id, StringComparer.OrdinalIgnoreCase) && !m.Enabled))
            {
                var r = Hf.Run(install, "mod-switch", "--id", m.Id, "--on", "yes");
                if (!r.Ok) res.Refused.Add(m.Name + ": " + r.Why);
            }
            foreach (var p in list.Problems) res.Refused.Add("HellForge: " + p);
            return res;
        }

        /// <summary>Every mod this target put into HellForge taken out. Returns how many.</summary>
        public static int Purge(string home, string install)
        {
            var mine = Load(home, install);
            int n = 0;
            foreach (var e in mine.Values.ToList())
            {
                var folder = System.IO.Path.Combine(Install.Mods(install), e.Folder ?? "\0");
                if (Directory.Exists(folder))
                {
                    var r = Hf.Run(install, "mod-remove", "--id", e.PackId);
                    if (!r.Ok && Directory.Exists(folder)) throw new HfError(e.Name + ": " + r.Why);
                }
                mine.Remove(e.QdId);
                n++;
            }
            Save(home, install, mine);
            return n;
        }

        /// <summary>The library mod's own Guid (its manifest.json), or null.</summary>
        static string OwnGuid(string modFolder)
        {
            try
            {
                var p = modFolder == null ? null : System.IO.Path.Combine(modFolder, "manifest.json");
                if (p == null || !File.Exists(p)) return null;
                var g = Json.Str(Json.Obj(File.ReadAllText(p, Encoding.UTF8)), "Guid");
                return g != null && Guid.TryParse(g, out var guid) ? guid.ToString("D") : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static string FreeName(string mods, string name)
        {
            var n = name;
            for (int i = 2; Directory.Exists(System.IO.Path.Combine(mods, n)) || File.Exists(System.IO.Path.Combine(mods, n + ".zip")); i++) n = name + " (" + i + ")";
            return n;
        }

        /// <summary>One mod's folder made (again): built under a dotted name HellForge skips, then
        /// put in place.</summary>
        static Entry Place(string install, Want w, string folder, Port.Report report, string officialData, string hfData)
        {
            var mods = Install.Mods(install);
            var final = System.IO.Path.Combine(mods, folder);
            var building = System.IO.Path.Combine(mods, ".hd2qd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(building);
            try
            {
                var files = w.AllFiles();
                bool sameDrive = files.Count > 0 && string.Equals(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(mods)),
                                               System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(files[0].Src)), StringComparison.OrdinalIgnoreCase);
                foreach (var (src, name) in files)
                {
                    var to = System.IO.Path.Combine(building, name);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                    if (!(sameDrive && CreateHardLink(to, src, IntPtr.Zero))) File.Copy(src, to, true);
                }
                if (w.Whole)
                {
                    var own = new Entry { QdId = w.Id, Folder = folder, PackId = Placed.IdOf(w.Path, folder), Name = w.Name, Level = Port.Level.Ready.ToString(), Library = w.Path };
                    if (Directory.Exists(final)) Directory.Delete(final, true);
                    Directory.Move(building, final);
                    return own;
                }
                if (w.Path != null && Directory.Exists(w.Path))
                    foreach (var top in Directory.EnumerateFiles(w.Path))
                    {
                        var name = System.IO.Path.GetFileName(top);
                        if (Placed.IsPatch(name) || string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                        var to = System.IO.Path.Combine(building, name);
                        if (File.Exists(to)) continue;
                        if (!(sameDrive && CreateHardLink(to, top, IntPtr.Zero))) File.Copy(top, to, true);
                    }
                var guid = Deployed.GuidOf(w.Id);
                Placed.WriteText(System.IO.Path.Combine(building, "manifest.json"), Json.Write(new Dictionary<string, object>
                {
                    ["Version"] = 1, ["Guid"] = guid, ["Name"] = w.Name, ["Description"] = "Deployed by HD2 Mod Quick Deploy from its library.",
                }));
                var entry = new Entry { QdId = w.Id, Folder = folder, PackId = "pack." + guid, Name = w.Name, Library = w.Path,
                                       Level = (w.MadeForHellForge ? Port.Level.Ready : Port.Level.Unknown).ToString() };
                if (report != null)
                {
                    entry.Notes = Port.Apply(report, officialData, hfData, null, building);
                    entry.Level = report.Level.ToString();
                    entry.Merged = report.Merged;
                    entry.LeftOutTypes = report.LeftOutTypes.ToDictionary(k => k.Key.ToString("x16"), k => k.Value);
                    entry.KeptDiffering = report.KeptDiffering.ToDictionary(k => k.Key.ToString("x16"), k => k.Value);
                }
                if (Directory.Exists(final)) Directory.Delete(final, true);
                // nothing of it is for HellForge's game (Boot Mod Loader: its boot only, 2026-11-11: an
                // empty pack HellForge listed as a problem): not placed at all
                if (!Directory.EnumerateFiles(building).Any(f => Placed.IsPatch(System.IO.Path.GetFileName(f)) && !f.EndsWith(".gpu_resources") && !f.EndsWith(".stream")))
                {
                    entry.Folder = null;
                    entry.Level = Port.Level.Partial.ToString();
                    return entry;
                }
                Directory.Move(building, final);
                return entry;
            }
            finally
            {
                if (Directory.Exists(building)) try { Directory.Delete(building, true); } catch (IOException) { }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(string newName, string existing, IntPtr security);
    }
}
