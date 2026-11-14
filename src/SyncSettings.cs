using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Qd.HellForge
{
    /// <summary>Mod sync's settings, which HellForge keeps in mods\hellforge-mods.json and documents
    /// for launchers of their own (external-launchers.md, "Mods"): "share" (default true) lets
    /// players who join download your gameplay mods, "listPublic" (default true) lists your Public
    /// lobbies on other players' Galactic Map, "showEditedStats" (default false) shows lobbies whose
    /// host enforces stat changes, "blocked" the mods mod sync never offers. No HellForge.Cli
    /// command sets them, so they are written here: the whole file, every other field kept, to a
    /// temporary file and then renamed, as HellForge does. The mods it downloaded from hosts are in
    /// mods\.synced (mods.md, "Downloaded mods"); HellForge's own launcher deletes them all at
    /// once, never while a session runs (the user, 2026-11-15: what the plugin can add).</summary>
    static class SyncSettings
    {
        public sealed class State
        {
            public bool Share = true, ListPublic = true, ShowEditedStats;
            public List<string> Blocked = new List<string>();
            /// <summary>The mods downloaded from hosts, and the room they take.</summary>
            public int Downloaded;
            public long DownloadedBytes;
        }

        static string FileOf(string install) => Path.Combine(Install.Mods(install), "hellforge-mods.json");

        /// <summary>mods\.synced: a folder for each mod downloaded (a dot folder in it is not one).</summary>
        public static string Synced(string install) => Path.Combine(Install.Mods(install), ".synced");

        static Dictionary<string, object> Doc(string install)
        {
            var file = FileOf(install);
            return (File.Exists(file) ? Json.Obj(File.ReadAllText(file, Encoding.UTF8)) : null) ?? new Dictionary<string, object> { ["format"] = 1L };
        }

        public static State Read(string install)
        {
            var d = Doc(install);
            var s = new State
            {
                Share = Json.MaybeBool(d, "share") ?? true,
                ListPublic = Json.MaybeBool(d, "listPublic") ?? true,
                ShowEditedStats = Json.MaybeBool(d, "showEditedStats") ?? false,
                Blocked = (Json.List(d, "blocked") ?? new List<object>()).OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            };
            var store = Synced(install);
            if (Directory.Exists(store))
                foreach (var dir in Directory.GetDirectories(store).Where(x => !Path.GetFileName(x).StartsWith(".")))
                {
                    s.Downloaded++;
                    try { s.DownloadedBytes += Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
            return s;
        }

        /// <summary>One of the switches ("share", "listPublic", "showEditedStats") set.</summary>
        public static void Set(string install, string key, bool on)
        {
            if (key != "share" && key != "listPublic" && key != "showEditedStats") throw new ArgumentException(key);
            var d = Doc(install);
            d[key] = on;
            Write(install, d);
        }

        /// <summary>A mod taken off the blocklist: lobbies running it are offered again.</summary>
        public static void Unblock(string install, string id)
        {
            var d = Doc(install);
            var blocked = (Json.List(d, "blocked") ?? new List<object>()).OfType<string>().ToList();
            if (blocked.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal)) == 0) return;
            d["blocked"] = blocked.Cast<object>().ToList();
            Write(install, d);
        }

        /// <summary>Every mod downloaded from hosts deleted (a session started later downloads again
        /// what it needs). The caller makes sure no session runs.</summary>
        public static void DeleteDownloads(string install)
        {
            var store = Synced(install);
            if (Directory.Exists(store)) Directory.Delete(store, true);
        }

        static void Write(string install, Dictionary<string, object> d)
        {
            var file = FileOf(install);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var tmp = file + ".hd2qd-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, Json.Write(d), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }
    }
}
