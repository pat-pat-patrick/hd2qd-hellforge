using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Qd.HellForge
{
    /// <summary>Library mods put into HellForge's mods\ folder by this plugin. HellForge's docs
    /// say to put mods there ("by copying a zip or folder there"), never into game\data: each
    /// session copies the enabled mods' patches into its game copy itself (ModStaging.Stage, read
    /// in HellForge 0.4.0-beta d8a8b46: it only reads mods\, a copy through a .staging name). So a
    /// placed mod is the library's files hard-linked into a folder of their own when both are on
    /// one drive, copied otherwise; the library's files are never written.
    ///
    /// Only folders recorded here (home\hellforge\placed.json) are this plugin's: it never
    /// changes or removes a mod it did not place.</summary>
    static class Placed
    {
        public sealed class Entry
        {
            /// <summary>The folder's name in mods\.</summary>
            public string Folder;
            /// <summary>The library mod's folder and name when it was placed.</summary>
            public string Library, Name;
            /// <summary>HellForge's id for it (pack.&lt;guid&gt;, pack.&lt;slug&gt; or a code mod's id).</summary>
            public string Id;
            /// <summary>Each file, relative, with "size|ticks" of the library's file when placed.</summary>
            public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public bool Linked;
            /// <summary>How it was made ready for HellForge's game (Port.Level), and against which
            /// two games ("official stamp|HellForge stamp"; null: not checked).</summary>
            public string Level, Games;
            public int Merged, LeftOut;
            /// <summary>Resources kept although their two originals differ, by type (hex).</summary>
            public Dictionary<string, int> KeptDiffering = new Dictionary<string, int>();
            /// <summary>Resources left out, by type (hex).</summary>
            public Dictionary<string, int> LeftOutTypes = new Dictionary<string, int>();
            public List<string> Notes = new List<string>();
        }

        static string RecordFile(string home) => Path.Combine(home, "hellforge", "placed.json");

        static string Key(string install) => Path.GetFullPath(install).TrimEnd('\\').ToLowerInvariant();

        /// <summary>The mods placed in this installation, by folder name.</summary>
        public static Dictionary<string, Entry> Load(string home, string install)
        {
            var all = ReadAll(home);
            var mine = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            if (!(Json.Obj(all, Key(install)) is Dictionary<string, object> d)) return mine;
            foreach (var kv in d)
                if (kv.Value is Dictionary<string, object> e)
                {
                    var entry = new Entry
                    {
                        Folder = kv.Key, Library = Json.Str(e, "library"), Name = Json.Str(e, "name"), Id = Json.Str(e, "id"),
                        Linked = Json.Bool(e, "linked"), Level = Json.Str(e, "level"), Games = Json.Str(e, "games"),
                        Merged = (int)Json.Long(e, "merged"), LeftOut = (int)Json.Long(e, "left_out"), Notes = Json.Strs(e, "notes"),
                    };
                    if (Json.Obj(e, "kept_differing") is Dictionary<string, object> kd)
                        foreach (var k in kd) entry.KeptDiffering[k.Key] = (int)Json.Long(kd, k.Key);
                    if (Json.Obj(e, "left_out_types") is Dictionary<string, object> lt)
                        foreach (var k in lt) entry.LeftOutTypes[k.Key] = (int)Json.Long(lt, k.Key);
                    if (Json.Obj(e, "files") is Dictionary<string, object> files)
                        foreach (var f in files) entry.Files[f.Key] = f.Value as string;
                    mine[kv.Key] = entry;
                }
            return mine;
        }

        static Dictionary<string, object> ReadAll(string home)
        {
            try
            {
                var p = RecordFile(home);
                return File.Exists(p) ? Json.Obj(File.ReadAllText(p, Encoding.UTF8)) ?? new Dictionary<string, object>() : new Dictionary<string, object>();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return new Dictionary<string, object>(); }
        }

        static void Save(string home, string install, Dictionary<string, Entry> mine)
        {
            var all = ReadAll(home);
            all[Key(install)] = mine.Values.ToDictionary(e => e.Folder, e => (object)new Dictionary<string, object>
            {
                ["library"] = e.Library, ["name"] = e.Name, ["id"] = e.Id, ["linked"] = e.Linked,
                ["files"] = e.Files.ToDictionary(f => f.Key, f => (object)f.Value),
                ["level"] = e.Level, ["games"] = e.Games, ["merged"] = e.Merged, ["left_out"] = e.LeftOut,
                ["kept_differing"] = e.KeptDiffering.ToDictionary(k => k.Key, k => (object)k.Value), ["notes"] = e.Notes.Cast<object>().ToList(),
                ["left_out_types"] = e.LeftOutTypes.ToDictionary(k => k.Key, k => (object)k.Value),
            });
            WriteText(RecordFile(home), Json.Write(all));
        }

        /// <summary>A whole file at once: a temporary file, then a rename.</summary>
        public static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>The placed entry for a library folder, or null.</summary>
        public static Entry Of(Dictionary<string, Entry> mine, string libraryFolder) =>
            mine.Values.FirstOrDefault(e => SamePath(e.Library, libraryFolder));

        public static bool SamePath(string a, string b) =>
            a != null && b != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- what HellForge will make of a folder

        static readonly Regex PatchName = new Regex(@"^[0-9a-fA-F]{16}\.patch_\d+(\.gpu_resources|\.stream)?$");
        static readonly Regex NotSlug = new Regex("[^a-z0-9-]+");

        public static bool IsPatch(string fileName) => PatchName.IsMatch(fileName);

        /// <summary>HellForge's slug of a pack's name (CosmeticPacks.Slug).</summary>
        public static string Slug(string name)
        {
            var slug = NotSlug.Replace(name.ToLowerInvariant(), "-").Trim('-');
            return slug.Length == 0 ? "pack" : slug.Length > 58 ? slug.Substring(0, 58).TrimEnd('-') : slug;
        }

        /// <summary>HellForge's own manifest in a folder (hellforge-mod.json, or bts-mod.json from
        /// before the rename), or null.</summary>
        public static string CodeManifest(string folder)
        {
            foreach (var n in new[] { "hellforge-mod.json", "bts-mod.json" })
                if (File.Exists(Path.Combine(folder, n))) return Path.Combine(folder, n);
            return null;
        }

        /// <summary>The id HellForge gives the folder once it is mods\&lt;folderName&gt;, or null when
        /// HellForge would not take it as a mod (no hellforge-mod.json, no patches at its root, no
        /// manifest.json naming options or a Guid: CosmeticPacks.IsPack).</summary>
        public static string IdOf(string folder, string folderName)
        {
            var code = CodeManifest(folder);
            if (code != null) return Json.Str(Json.Obj(File.ReadAllText(code, Encoding.UTF8)), "id");
            var manifest = Json.Obj(ReadIf(Path.Combine(folder, "manifest.json")));
            bool rootPatches = Directory.EnumerateFiles(folder).Any(f => IsPatch(Path.GetFileName(f)));
            bool isPack = rootPatches || manifest != null && (manifest.ContainsKey("Options") || manifest.ContainsKey("Guid"));
            if (!isPack) return null;
            var guid = Json.Str(manifest, "Guid");
            if (guid != null && Guid.TryParse(guid, out var g)) return "pack." + g.ToString("D");
            var name = Json.Str(manifest, "Name") is string n && n.Length > 0 ? n : folderName;
            return "pack." + Slug(name);
        }

        static string ReadIf(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>A folder name for a library mod: its name without characters Windows refuses,
        /// never starting with a dot (HellForge skips those).</summary>
        public static string FolderName(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var s = new string((name ?? "").Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim().TrimStart('.').TrimEnd('.', ' ');
            s = Regex.Replace(s, @"\s+", " ");
            if (s.Length > 80) s = s.Substring(0, 80).TrimEnd('.', ' ');
            return s.Length == 0 ? "mod" : s;
        }

        // ---------------------------------------------------------------- placing and removing

        /// <summary>Puts a library mod into HellForge's mods\ folder. Refused (HfError) when
        /// HellForge would not take the folder as a mod, or when a mod of the same id is there
        /// already and this plugin did not place it (one of the player's own copies, which is never
        /// replaced).</summary>
        public static Entry Put(string home, string install, string name, string libraryFolder, Hf.ModList current, string officialData = null)
        {
            if (!Directory.Exists(libraryFolder)) throw new Refusal("no_library_folder", libraryFolder);
            var mine = Load(home, install);
            var already = Of(mine, libraryFolder);
            if (already != null) return already;
            var mods = Install.Mods(install);
            var folderName = FolderName(name);
            for (int i = 2; Directory.Exists(Path.Combine(mods, folderName)) || File.Exists(Path.Combine(mods, folderName + ".zip")); i++)
                folderName = FolderName(name) + " (" + i + ")";
            var id = IdOf(libraryFolder, folderName) ?? throw new Refusal("not_a_mod");
            var clash = current?.Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (clash != null) throw new Refusal("same_id", clash.Name);
            var entry = new Entry { Folder = folderName, Library = Path.GetFullPath(libraryFolder), Name = name, Id = id };
            Copy(libraryFolder, Path.Combine(mods, folderName), entry);
            Ready(home, install, officialData, entry);
            mine[folderName] = entry;
            Save(home, install, mine);
            return entry;
        }

        /// <summary>A refusal the window words (Lang key "refuse_" + Key).</summary>
        public sealed class Refusal : HfError
        {
            public readonly string Key, Arg;
            public Refusal(string key, string arg = null) : base(key + (arg != null ? ": " + arg : "")) { Key = key; Arg = arg; }
        }

        static void Copy(string from, string to, Entry entry)
        {
            Directory.CreateDirectory(to);
            bool sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(from)), Path.GetPathRoot(Path.GetFullPath(to)), StringComparison.OrdinalIgnoreCase);
            bool allLinked = sameDrive;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(from.TrimEnd('\\').Length + 1);
                var target = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target)) File.Delete(target);
                if (!(sameDrive && CreateHardLink(target, file, IntPtr.Zero)))
                {
                    File.Copy(file, target);
                    allLinked = false;
                }
                entry.Files[rel] = Stamp(file);
            }
            entry.Linked = allLinked;
        }

        static string Stamp(string file)
        {
            var fi = new FileInfo(file);
            return fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        }

        /// <summary>Whether the library's files differ from the ones placed (changed, added or
        /// removed since).</summary>
        public static bool Stale(Entry e)
        {
            if (e.Library == null || !Directory.Exists(e.Library)) return false;
            var now = Directory.EnumerateFiles(e.Library, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f.Substring(e.Library.TrimEnd('\\').Length + 1), Stamp, StringComparer.OrdinalIgnoreCase);
            return now.Count != e.Files.Count || now.Any(kv => !e.Files.TryGetValue(kv.Key, out var s) || s != kv.Value);
        }

        /// <summary>The placed folder made again from the library (after the library mod changed).</summary>
        public static Entry Renew(string home, string install, Entry e, string officialData = null)
        {
            var mine = Load(home, install);
            if (!mine.ContainsKey(e.Folder)) throw new HfError("not placed: " + e.Folder);
            var folder = OwnFolder(install, e.Folder);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var fresh = new Entry { Folder = e.Folder, Library = e.Library, Name = e.Name, Id = IdOf(e.Library, e.Folder) ?? e.Id };
            Copy(e.Library, folder, fresh);
            Ready(home, install, officialData, fresh);
            mine[e.Folder] = fresh;
            Save(home, install, mine);
            return fresh;
        }

        /// <summary>HellForge's copy made ready for its game (Port): what differs between the two games
        /// is merged or left out in the copy; the result is recorded in the entry.</summary>
        static void Ready(string home, string install, string officialData, Entry e)
        {
            var hfData = Path.Combine(install, "game", "data");
            if (officialData == null || !File.Exists(Path.Combine(officialData, "bundles.nxa")) || !File.Exists(Path.Combine(hfData, "bundles.nxa")))
            {
                e.Level = Port.Level.Unknown.ToString();
                return;
            }
            var r = Port.Check(home, officialData, hfData, e.Library);
            e.Notes = Port.Apply(r, officialData, hfData, e.Library, OwnFolder(install, e.Folder));
            e.Level = r.Level.ToString();
            e.Games = Games(officialData, hfData);
            e.Merged = r.Merged; e.LeftOut = r.LeftOut;
            e.KeptDiffering = r.KeptDiffering.ToDictionary(k => k.Key.ToString("x16"), k => k.Value);
            e.LeftOutTypes = r.LeftOutTypes.ToDictionary(k => k.Key.ToString("x16"), k => k.Value);
        }

        /// <summary>The two games' stamps, as an entry records them.</summary>
        public static string Games(string officialData, string hfData)
        {
            try { return GameIndex.Stamp(officialData) + "|" + GameIndex.Stamp(hfData); }
            catch (IOException) { return null; }
        }

        /// <summary>Whether the copy was made ready against other games than these (either was updated).</summary>
        public static bool GamesChanged(Entry e, string install, string officialData)
        {
            if (officialData == null || !File.Exists(Path.Combine(officialData, "bundles.nxa"))) return false;
            var now = Games(officialData, Path.Combine(install, "game", "data"));
            return now != null && now != e.Games;
        }

        /// <summary>A placed mod taken out of HellForge: HellForge's own mod-remove, then the record.
        /// The library is not touched.</summary>
        public static void Remove(string home, string install, Entry e)
        {
            var mine = Load(home, install);
            if (!mine.ContainsKey(e.Folder)) throw new HfError("not placed: " + e.Folder);
            var folder = OwnFolder(install, e.Folder);
            if (Directory.Exists(folder))
            {
                var r = Hf.Run(install, "mod-remove", "--id", e.Id);
                if (!r.Ok && Directory.Exists(folder)) throw new HfError(r.Why);
            }
            mine.Remove(e.Folder);
            Save(home, install, mine);
        }

        /// <summary>Records of mods whose folder is gone from mods\ (removed in HellForge.exe) dropped.</summary>
        public static void Forget(string home, string install)
        {
            var mine = Load(home, install);
            var gone = mine.Values.Where(e => !Directory.Exists(OwnFolder(install, e.Folder))).Select(e => e.Folder).ToList();
            if (gone.Count == 0) return;
            foreach (var g in gone) mine.Remove(g);
            Save(home, install, mine);
        }

        /// <summary>A placed folder's path, only ever inside the installation's mods\.</summary>
        static string OwnFolder(string install, string name)
        {
            var mods = Path.GetFullPath(Install.Mods(install)).TrimEnd('\\');
            var p = Path.GetFullPath(Path.Combine(mods, name));
            if (!string.Equals(Path.GetDirectoryName(p), mods, StringComparison.OrdinalIgnoreCase) || name.StartsWith("."))
                throw new HfError("not a folder of mods\\: " + name);
            return p;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(string newName, string existing, IntPtr security);
    }
}
