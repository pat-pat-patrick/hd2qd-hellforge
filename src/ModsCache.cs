using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Hd2Qd.HellForge
{
    /// <summary>HellForge's mod list (HellForge.Cli mods), kept while what it is read from is
    /// unchanged. The command reads every mod each time: 0.74 s on 41 mods (2026-11-15), the
    /// largest fixed part of a deploy. A deploy reads it before placing, when the mods folder is
    /// as the last deploy left it, so after each deploy the list is read again in the background
    /// and the next deploy finds it ready (the user, 2026-11-15).
    ///
    /// What the list is read from (likely, checked 2026-11-15 on 0.4.0-beta): HellForge's own
    /// program, and its mods folder: every mod HellForge lists, its own ones too, has its folder
    /// there, and so has the settings file (hellforge-mods.json). The key is every entry of that
    /// folder at any depth (name, size, time written) and app\cli's files, so a change made
    /// anywhere there, by HellForge.exe, by hand or by a deploy, reads the list again.</summary>
    static class ModsCache
    {
        /// <summary>Tests only: the refresh after a deploy done at once instead of on a thread.</summary>
        internal static bool Inline;

        static readonly object gate = new object();
        static Task pending = Task.CompletedTask;

        static string FileOf(string home) => Path.Combine(home, "hellforge", "mods_list.json");

        /// <summary>The list: the kept one when its key is still right, else HellForge's answer
        /// (kept for next time when nothing changed while it was read).</summary>
        public static Hf.ModList Read(string home, string install)
        {
            // a refresh under way is waited for: it is the same read, already started
            Task running;
            lock (gate) running = pending;
            if (!running.IsCompleted) running.Wait(TimeSpan.FromSeconds(10));
            var key = Key(install);
            var kept = Load(home, key);
            if (kept != null) return Hf.ParseMods(kept);
            var fresh = Hf.Ask(install, "mods");
            if (Key(install) == key) Store(home, key, fresh);
            return Hf.ParseMods(fresh);
        }

        /// <summary>After a deploy: the list read again on a thread of its own, unless the kept one
        /// is still right. A failure leaves the next deploy to read it, as before.</summary>
        public static void RefreshLater(string home, string install)
        {
            Action work = () =>
            {
                try
                {
                    var key = Key(install);
                    if (Load(home, key) != null) return;
                    var fresh = Hf.Ask(install, "mods");
                    if (Key(install) == key) Store(home, key, fresh);
                }
                catch (Exception e) when (e is HfError || e is IOException || e is UnauthorizedAccessException) { }
            };
            if (Inline) { work(); return; }
            lock (gate) pending = pending.IsCompleted ? Task.Run(work) : pending.ContinueWith(_ => work());
        }

        /// <summary>What the list is read from, as one hash: each entry of the mods folder (a file
        /// by its path, size and time written, a folder by its path; a link is not followed), and
        /// app\cli's files.</summary>
        internal static string Key(string install) => Hash(Lines(install));

        internal static string Hash(List<string> lines)
        {
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).Replace("-", "");
        }

        internal static List<string> Lines(string install)
        {
            var lines = new List<string>();
            void Walk(DirectoryInfo dir, string rel)
            {
                foreach (var e in dir.EnumerateFileSystemInfos())
                {
                    var name = rel + e.Name;
                    if (e is FileInfo f) lines.Add("f|" + name + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks);
                    else
                    {
                        // a folder by its name only: its time written moves a moment after a file in
                        // it was written (proven: 1 ms later, 2026-11-15), and what it holds is listed anyway
                        lines.Add("d|" + name);
                        if ((e.Attributes & FileAttributes.ReparsePoint) == 0) Walk((DirectoryInfo)e, name + "\\");
                    }
                }
            }
            var mods = new DirectoryInfo(Install.Mods(install));
            if (mods.Exists) Walk(mods, "mods\\");
            var cli = new DirectoryInfo(Path.GetDirectoryName(Install.Cli(install)));
            if (cli.Exists)
                foreach (var f in cli.EnumerateFiles()) lines.Add("c|" + f.Name + "|" + f.Length + "|" + f.LastWriteTimeUtc.Ticks);
            lines.Sort(StringComparer.Ordinal);
            lines.Insert(0, Path.GetFullPath(install).TrimEnd('\\').ToLowerInvariant());
            return lines;
        }

        static Dictionary<string, object> Load(string home, string key)
        {
            try
            {
                var file = FileOf(home);
                if (!File.Exists(file)) return null;
                var d = Json.Obj(File.ReadAllText(file, Encoding.UTF8));
                return d != null && Json.Str(d, "key") == key ? Json.Obj(d, "mods") : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is ArgumentException) { return null; }
        }

        static void Store(string home, string key, Dictionary<string, object> raw)
        {
            try
            {
                var file = FileOf(home);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var tmp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                File.WriteAllText(tmp, Json.Write(new Dictionary<string, object> { ["key"] = key, ["mods"] = raw }), new UTF8Encoding(false));
                lock (gate)
                {
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmp, file);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }
}
