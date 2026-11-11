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
                // HellForge's game is build 25480438, from before the official game's 2026-10-20
                // update: it runs LuaJIT with ffi, bit and jit (proven: the game copy's
                // bin\lua51.dll names itself LuaJIT 2.1.0-alpha and carries ffi)
                ["lua"] = "luajit",
                // and its Lua mods only through Bingus Shared Loader (HellForge's own rule: no Boot
                // Mod Loader or MDL3; its mods page says Lua mods need Bingus Shared Loader)
                ["loaders"] = new List<object> { "bsl" },
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
            var states = new Dictionary<string, object>();
            try
            {
                var wants = Mods(host);
                var each = Deployed.States(home, install, Data(host), wants);
                pending = Deployed.Pending(home, install, wants, each);
                foreach (var kv in each) states[kv.Key] = kv.Value;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            // each enabled mod's state, for the manager's rows ("deployed" / "pending")
            return new Dictionary<string, object> { ["pending"] = (long)pending, ["running"] = running, ["mods"] = states };
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
            // a code mod with sources placed: compiled now, with the deploy's progress, so the
            // next start does not wait for it (the user, 2026-11-12: "部署时编译，显示进度")
            if (r.SourcePlaced > 0) BuildSources(install, host, lang, r);
            try
            {
                var list = r.List ?? Hf.ReadMods(install);
                var again = Coop.Reapply(home, install, list);
                // a code mod placed now is not in the list read before placing: its group (gameplay or
                // per-player) is HellForge's to say, so the marks read the list again, only then (and
                // MarkAll reads nothing while "every gameplay mod only for me" is off)
                foreach (var refused in Coop.MarkAll(home, install, again.Count > 0 || r.CodePlaced > 0 ? null : list).Refused) r.Refused.Add(refused);
                r.Step("co-op marks");
            }
            catch (Exception e) when (e is HfError || e is IOException || e is UnauthorizedAccessException) { r.Refused.Add(e.Message); }
            notesAt = DateTime.MinValue;
            return new Dictionary<string, object>
            {
                ["said"] = Words.F(lang, "target_said", r.Mods, r.Placed, r.Kept, r.Removed),
                ["refused"] = r.Refused.Cast<object>().ToList(),
                // for the manager's log: what took the time
                ["log"] = string.Join(", ", r.Steps),
            };
        }

        /// <summary>HellForge's own build of its source mods (build-source-mods): it fetches its
        /// pinned Zig when it has none yet, then compiles what changed. Its progress file is read
        /// while it runs and said through the host's progress(text), when the manager gives one;
        /// what did not compile is refused, as the session would leave it out.</summary>
        static void BuildSources(string install, Dictionary<string, object> host, string lang, Deployed.Result r)
        {
            var progress = host.TryGetValue("progress", out var p) ? p as Action<string> : null;
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hd2qd-hellforge-build-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            var run = System.Threading.Tasks.Task.Run(() => Hf.Run(install, "build-source-mods", "--install", install, "--progress", file));
            string last = null;
            while (!run.Wait(300))
            {
                var line = BuildLine(file, lang);
                if (line != null && line != last) { progress?.Invoke(line); last = line; }
            }
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
            var res = run.Result;
            if (!res.Ok) { r.Refused.Add(Words.F(lang, "build_failed", res.Why)); r.Step("source mods not built"); return; }
            var d = Json.Obj(res.Out);
            foreach (var x in Json.List(d, "problems"))
                r.Refused.Add(Words.F(lang, "build_problem", x is string s ? s : x is Dictionary<string, object> o ? (Json.Str(o, "message") ?? Json.Str(o, "why") ?? Json.Write(o)) : Convert.ToString(x)));
            r.Step("source mods built (" + Json.Long(d, "built") + ")");
        }

        /// <summary>A line for HellForge's build progress file ({stage, done, total, mod}), or null.</summary>
        internal static string BuildLine(string file, string lang)
        {
            Dictionary<string, object> b;
            try { b = File.Exists(file) ? Json.Obj(File.ReadAllText(file)) : null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
            if (b == null) return null;
            var stage = Json.Str(b, "stage");
            if (stage == "tools") return Words.Get(lang, "build_tools");
            if (stage != "done" && Json.Long(b, "total") > 0 && Json.Str(b, "mod") is string mod && mod.Length > 0)
                return Words.F(lang, "build_mod", mod, Json.Long(b, "done"), Json.Long(b, "total"));
            return null;
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
            /// <summary>How its files are in HellForge, as the manager's rows say it for the official
            /// game: "link" (hard links to the library's files), "copy" (another drive), "converted"
            /// (files the conversion wrote, Port.Apply); null in a record made before 0.8.4.</summary>
            public string How;
            public int Merged;
            public Dictionary<string, int> LeftOutTypes = new Dictionary<string, int>(), KeptDiffering = new Dictionary<string, int>();
            public List<string> Notes = new List<string>();
        }

        public sealed class Result
        {
            public int Mods, Placed, Kept, Removed;
            /// <summary>Code mods with sources this deploy placed: HellForge compiles them, so the
            /// deploy has it do so now, not the next session.</summary>
            public int SourcePlaced;
            /// <summary>Code mods (hellforge-mod.json) this deploy placed.</summary>
            public int CodePlaced;
            public List<string> Refused = new List<string>();
            /// <summary>HellForge's mod list after the deploy placed and removed (its order and
            /// switches aside): the co-op marks are read from it, not read again.</summary>
            public Hf.ModList List;
            /// <summary>Each step and its time, for the manager's log ("read 1.4 s, ...").</summary>
            public List<string> Steps = new List<string>();
            readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            public void Step(string what)
            {
                Steps.Add(what + " " + clock.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s");
                clock.Restart();
            }
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
                        How = Json.Str(e, "how"),
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
                ["merged"] = e.Merged, ["notes"] = e.Notes.Cast<object>().ToList(), ["library"] = e.Library, ["how"] = e.How,
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
        /// <summary>Each mod of the list in use: "pending" (a deploy would place it again, the
        /// same test as Pending), else how it is in HellForge as the library has it: "link",
        /// "copy" or "converted" (Entry.How).</summary>
        public static Dictionary<string, string> States(string home, string install, string officialData, List<Want> wants)
        {
            var mine = Load(home, install);
            var games = Games(officialData, install);
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in wants.Where(w => w.Id != null))
                states[w.Id] = !mine.TryGetValue(w.Id, out var e) || e.Signature != Signature(w, games)
                               || (e.Folder != null && !Directory.Exists(System.IO.Path.Combine(Install.Mods(install), e.Folder))) ? "pending" : HowOf(install, e);
            return states;
        }

        static readonly Dictionary<string, string> howSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How a placed mod is in HellForge: its record's How, else (a record made before
        /// 0.8.4) read once from its files' link counts: one file that is no hard link makes it a
        /// copy, or converted when the conversion wrote to it.</summary>
        static string HowOf(string install, Entry e)
        {
            if (e.How != null) return e.How;
            if (e.Folder == null) return "link";
            var folder = System.IO.Path.Combine(Install.Mods(install), e.Folder);
            var key = folder + "|" + e.Signature;
            lock (howSeen)
            {
                if (howSeen.TryGetValue(key, out var had)) return had;
                bool single = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                                       .Where(f => Placed.IsPatch(System.IO.Path.GetFileName(f))).Any(f => Links(f) == 1);
                var how = !single ? "link" : e.Merged > 0 || e.LeftOutTypes.Count > 0 ? "converted" : "copy";
                howSeen[key] = how;
                return how;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FileInfoByHandle
        {
            public uint Attributes;
            public long Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(IntPtr file, out FileInfoByHandle info);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        /// <summary>A file's hard-link count (1: no other name), 0 when it cannot be read.</summary>
        static uint Links(string path)
        {
            var h = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h == new IntPtr(-1)) return 0;
            try { return GetFileInformationByHandle(h, out var i) ? i.Links : 0; }
            finally { CloseHandle(h); }
        }

        public static int Pending(string home, string install, string officialData, List<Want> wants) =>
            Pending(home, install, wants, States(home, install, officialData, wants));

        /// <summary>Pending from States already made: the mods a deploy would place again, and
        /// those it would take out.</summary>
        public static int Pending(string home, string install, List<Want> wants, Dictionary<string, string> states) =>
            states.Values.Count(v => v == "pending")
            + Load(home, install).Keys.Count(id => wants.All(w => !string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase)));

        public static Result Run(string home, string install, string officialData, List<Want> wants, string lang)
        {
            var res = new Result();
            var mods = Install.Mods(install);
            var mine = Load(home, install);
            var games = Games(officialData, install);
            res.Step("games compared");
            // HellForge's mods command reads every mod (1.45 s on 39 mods, 2026-11-12): read once
            // here, and again only when this deploy placed or removed one
            var list = Hf.ReadMods(install);
            res.Step("HellForge's list read");
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
            res.Step("unchanged found (" + res.Kept + ")");
            var hfData = HfData(install);
            var reports = toPlace.Count > 0 && games != "none"
                ? Port.CheckSets(home, officialData, hfData, toPlace.Where(w => !w.Whole && !w.MadeForHellForge).ToDictionary(w => w.Id, w => w.Files.Select(f => (f.Src, f.Name)).ToList(), StringComparer.OrdinalIgnoreCase))
                : new Dictionary<string, Port.Report>();
            if (toPlace.Count > 0) res.Step("compared for HellForge's game (" + toPlace.Count + ")");
            foreach (var w in toPlace)
            {
                try
                {
                    mine.TryGetValue(w.Id, out var old);
                    // the folder follows the mod's name: a new version ("… v1.229.0") gets a folder of
                    // that name, and the old one ("… v1.226.1") goes once the new one is in place
                    var named = Placed.FolderName(w.Name);
                    var folder = old?.Folder != null && string.Equals(old.Folder, named, StringComparison.OrdinalIgnoreCase) ? old.Folder : FreeName(mods, named);
                    var entry = Place(install, w, folder, reports.TryGetValue(w.Id, out var rep) ? rep : null, officialData, hfData);
                    if (old?.Folder != null && !string.Equals(old.Folder, entry.Folder, StringComparison.OrdinalIgnoreCase))
                    {
                        var was = System.IO.Path.Combine(mods, old.Folder);
                        try { if (Directory.Exists(was)) Directory.Delete(was, true); }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { res.Refused.Add(w.Name + ": " + old.Folder + ": " + e.Message); }
                    }
                    entry.Signature = Signature(w, games);
                    entry.Games = games;
                    mine[w.Id] = entry;
                    if (entry.Folder == null) { res.Refused.Add(Words.F(lang, "target_nothing_left", w.Name)); continue; }
                    ours.Add(entry.PackId);
                    res.Placed++;
                    if (w.Whole) res.CodePlaced++;
                    if (w.Whole && w.Path != null && Directory.Exists(System.IO.Path.Combine(w.Path, "src"))) res.SourcePlaced++;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
                {
                    res.Refused.Add(w.Name + ": " + e.Message);
                }
            }
            Save(home, install, mine);

            // mods no longer on: their folders taken out. They are this target's own folders, so
            // they are deleted here, not through HellForge's mod-remove: every HellForge.Cli call
            // is a process of its own, 0.4-0.8 s (2026-11-12), and HellForge.exe watches the folder
            var removedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in mine.Values.Where(e => wants.All(w => !string.Equals(w.Id, e.QdId, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                var folder = System.IO.Path.Combine(mods, gone.Folder ?? "\0");
                if (gone.Folder != null && Directory.Exists(folder))
                    try { Directory.Delete(folder, true); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { res.Refused.Add(gone.Name + ": " + e.Message); continue; }
                if (gone.PackId != null) removedIds.Add(gone.PackId);
                mine.Remove(gone.QdId);
                res.Removed++;
            }
            Save(home, install, mine);
            if (res.Placed > 0 || res.Removed > 0) res.Step("placed " + res.Placed + ", removed " + res.Removed);

            // the order (the manager's last wins; HellForge's top one does) and every one on,
            // written into hellforge-mods.json as HellForge documents for launchers
            // (external-launchers.md, "Mods": whole file, temporary file then rename), not through
            // mod-order and mod-switch; the list read before placing says the rest of the order
            res.List = list;
            var order = wants.Where(w => mine.TryGetValue(w.Id, out var e) && e.Folder != null && e.PackId != null).Select(w => mine[w.Id].PackId).Reverse().ToList();
            try
            {
                if (WriteModsFile(list.SettingsFile ?? System.IO.Path.Combine(mods, "hellforge-mods.json"), order, list.Mods.Select(m => m.Id).ToList(), removedIds))
                    res.Step("order and switches written");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { res.Refused.Add("hellforge-mods.json: " + e.Message); }
            foreach (var p in list.Problems) res.Refused.Add("HellForge: " + p);
            return res;
        }

        /// <summary>HellForge's mod state file brought to the deploy's order: ours first, top first,
        /// then the others in the order HellForge had them (as mod-order does); ours taken out of
        /// "disabled" (mods are on unless it lists them); the removed ones taken out of both. Every
        /// other field kept as it was. False when it already was so (nothing written).</summary>
        internal static bool WriteModsFile(string file, List<string> ours, List<string> listed, ICollection<string> removed)
        {
            Dictionary<string, object> doc = null;
            if (File.Exists(file)) doc = Json.Obj(File.ReadAllText(file, Encoding.UTF8));
            doc = doc ?? new Dictionary<string, object> { ["format"] = 1L };
            var cmp = StringComparer.OrdinalIgnoreCase;
            var oldOrder = (Json.List(doc, "order") ?? new List<object>()).OfType<string>().ToList();
            var oldOff = (Json.List(doc, "disabled") ?? new List<object>()).OfType<string>().ToList();
            var rest = listed.Concat(oldOrder).Where(id => !ours.Contains(id, cmp) && !removed.Contains(id)).Distinct(cmp);
            var order = ours.Concat(rest).ToList();
            var off = oldOff.Where(id => !ours.Contains(id, cmp) && !removed.Contains(id)).ToList();
            if (order.SequenceEqual(oldOrder, cmp) && off.SequenceEqual(oldOff, cmp)) return false;
            doc["order"] = order.Cast<object>().ToList();
            doc["disabled"] = off.Cast<object>().ToList();
            if (Json.Obj(doc, "packs") is Dictionary<string, object> packs)
                foreach (var id in removed) packs.Remove(id);
            var tmp = file + ".hd2qd-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, Json.Write(doc), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
            return true;
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
                bool copied = false;
                bool sameDrive = files.Count > 0 && string.Equals(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(mods)),
                                               System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(files[0].Src)), StringComparison.OrdinalIgnoreCase);
                foreach (var (src, name) in files)
                {
                    var to = System.IO.Path.Combine(building, name);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
                    if (!(sameDrive && CreateHardLink(to, src, IntPtr.Zero))) { File.Copy(src, to, true); copied = true; }
                }
                if (w.Whole)
                {
                    var own = new Entry { QdId = w.Id, Folder = folder, PackId = Placed.IdOf(w.Path, folder), Name = w.Name, Level = Port.Level.Ready.ToString(), Library = w.Path,
                                          How = copied ? "copy" : "link" };
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
                        if (!(sameDrive && CreateHardLink(to, top, IntPtr.Zero))) { File.Copy(top, to, true); copied = true; }
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
                // the conversion wrote (or took away) a patch file: no longer the library's own file
                entry.How = report != null && report.Steps.Count > 0 ? "converted" : copied ? "copy" : "link";
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
