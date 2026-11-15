using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Qd.HellForge
{
    /// <summary>Before HellForge is reinstalled (its uninstall deletes the whole installation folder,
    /// docs README "Uninstall"), what the player keeps in it is copied to home\hellforge\reinstall\
    /// &lt;time&gt;: profiles and saves, the game's and the mods' settings, the launcher's settings,
    /// Local Stats and the in-game editor's own files (the user, 2026-11-16). Mods are not: the
    /// library's come back with a deploy, and one HellForge has from elsewhere is put into the
    /// library first (Collect). After the reinstall, Restore puts back what the HellForge now there
    /// can take: a backup of another version gives less (Kind).</summary>
    static class Reinstall
    {
        public static string Root(string home) => Path.Combine(home, "hellforge", "reinstall");

        const string Manifest = "backup.json";
        const string EditorFolder = "hellforge-editor";
        const string LocalStatsFolder = "zz.local.stats";
        const string ModList = "hellforge-mods.json";

        /// <summary>What the installation holds for its player, relative to it ('/' for a folder):
        /// each with the backups it is restored from (Kind).</summary>
        static readonly (string Path, Kind[] Into)[] Parts =
        {
            ("profiles/", new[] { Kind.Same, Kind.Newer }),
            ("userdata/", new[] { Kind.Same, Kind.Newer }),
            ("launcher-settings.json", new[] { Kind.Same, Kind.Newer }),
            ("account-snapshot/", new[] { Kind.Same, Kind.Newer }),
            ("mods/" + LocalStatsFolder + "/", new[] { Kind.Same, Kind.Newer }),
            // what the editor keeps for its player (BundledMods.Kept); HellForge itself carries it into a newer editor
            ("mods/" + EditorFolder + "/editor-settings.json", new[] { Kind.Same, Kind.Newer }),
            ("mods/" + EditorFolder + "/mod-stats.json", new[] { Kind.Same, Kind.Newer }),
            ("mods/" + EditorFolder + "/presets/", new[] { Kind.Same, Kind.Newer }),
            // the mod list names this version's own mods and their switches: the same version only
            ("mods/" + ModList, new[] { Kind.Same }),
        };

        /// <summary>Under userdata\, made again by the game and never the player's: left out.</summary>
        static readonly string[] Skipped = { "userdata/shader_cache/", "userdata/environment/Temp/" };

        /// <summary>The installation now there against the one backed up: the same build, a newer
        /// one (it reads what an older one wrote), or an older or unknown one (it may not read a
        /// newer one's profiles: only the mods' own settings files go back).</summary>
        public enum Kind { Same, Newer, Older }

        public sealed class Backup
        {
            public string Folder, Version, Commit, Install;
            public DateTime When;
            public List<string> NotInLibrary = new List<string>();
            public string Name => Path.GetFileName(Folder);
        }

        /// <summary>The installation's version and commit (app\app-build.json), or nulls.</summary>
        public static (string Version, string Commit) VersionOf(string install)
        {
            try
            {
                var d = Json.Obj(File.ReadAllText(Path.Combine(install, "app", "app-build.json"), Encoding.UTF8));
                return (Json.Str(d, "version"), Json.Str(d, "commit"));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return (null, null); }
        }

        /// <summary>The backup made now. notInLibrary: the mods HellForge has from elsewhere, named in
        /// the backup so a restore can say they were not kept.</summary>
        public static Backup Make(string home, string install, IEnumerable<string> notInLibrary)
        {
            var (version, commit) = VersionOf(install);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var folder = Path.Combine(Root(home), stamp);
            for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(Root(home), stamp + "-" + n);
            var files = Path.Combine(folder, "files");
            Directory.CreateDirectory(files);
            foreach (var rel in Kept(install)) Copy(Path.Combine(install, Os(rel)), Path.Combine(files, Os(rel)));
            var b = new Backup { Folder = folder, Version = version, Commit = commit, Install = Path.GetFullPath(install), When = DateTime.Now, NotInLibrary = notInLibrary.ToList() };
            Placed.WriteText(Path.Combine(folder, Manifest), Json.Write(new Dictionary<string, object>
            {
                ["version"] = version, ["commit"] = commit, ["install"] = b.Install,
                ["when"] = b.When.ToString("o", CultureInfo.InvariantCulture),
                ["notInLibrary"] = b.NotInLibrary.Cast<object>().ToList(),
            }));
            return b;
        }

        /// <summary>Every file the backup takes, relative ('/'): the parts that exist, and the mods
        /// folder's own files (a mod's settings beside the mods, as armored-overhaul.ini).</summary>
        static List<string> Kept(string install)
        {
            var list = new List<string>();
            foreach (var (part, _) in Parts)
            {
                var full = Path.Combine(install, Os(part));
                if (part.EndsWith("/"))
                {
                    if (!Directory.Exists(full)) continue;
                    foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    {
                        var rel = Rel(install, f);
                        if (!Skipped.Any(s => rel.StartsWith(s, StringComparison.OrdinalIgnoreCase))) list.Add(rel);
                    }
                }
                else if (File.Exists(full)) list.Add(part);
            }
            var mods = Path.Combine(install, "mods");
            if (Directory.Exists(mods))
                foreach (var f in Directory.EnumerateFiles(mods))
                {
                    var name = Path.GetFileName(f);
                    // HellForge's own: its mod list (a part of its own), its locks and records (dot files)
                    if (name.StartsWith(".") || name.Equals(ModList, StringComparison.OrdinalIgnoreCase)) continue;
                    // a zipped mod is a mod, not a setting: it goes through the library
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add("mods/" + name);
                }
            return list;
        }

        /// <summary>The backups, newest first; one whose record cannot be read is left out.</summary>
        public static List<Backup> List(string home)
        {
            var all = new List<Backup>();
            var root = Root(home);
            if (!Directory.Exists(root)) return all;
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    var d = Json.Obj(File.ReadAllText(Path.Combine(dir, Manifest), Encoding.UTF8));
                    if (d == null) continue;
                    DateTime.TryParse(Json.Str(d, "when"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when);
                    all.Add(new Backup
                    {
                        Folder = dir, Version = Json.Str(d, "version"), Commit = Json.Str(d, "commit"), Install = Json.Str(d, "install"), When = when,
                        NotInLibrary = Json.List(d, "notInLibrary").OfType<string>().ToList(),
                    });
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return all.OrderByDescending(b => b.When).ToList();
        }

        static readonly Regex Numbers = new Regex(@"^(\d+)\.(\d+)\.(\d+)");

        /// <summary>What the installation now there is to the backup's. The same commit is the same
        /// build; a higher x.y.z is newer; anything else (lower, equal numbers of another build such
        /// as a nightly against a release, or a version not read) counts as older, the safe side.</summary>
        public static Kind Compare(Backup b, string version, string commit)
        {
            if (b.Commit != null && commit != null && b.Commit == commit && b.Version == version) return Kind.Same;
            var was = Numbers.Match(b.Version ?? "");
            var now = Numbers.Match(version ?? "");
            if (!was.Success || !now.Success) return Kind.Older;
            for (int i = 1; i <= 3; i++)
            {
                int a = int.Parse(now.Groups[i].Value, CultureInfo.InvariantCulture), c = int.Parse(was.Groups[i].Value, CultureInfo.InvariantCulture);
                if (a != c) return a > c ? Kind.Newer : Kind.Older;
            }
            return Kind.Older;
        }

        public sealed class Outcome
        {
            public Kind Kind;
            public int Restored;
            /// <summary>The parts left out for this version, relative ('/').</summary>
            public List<string> LeftOut = new List<string>();
            /// <summary>Where the files a restore replaced were put first.</summary>
            public string Replaced;
        }

        /// <summary>The backup put into the installation: the parts this version can take; a file it
        /// replaces is copied beside the backup first (replaced-&lt;time&gt;).</summary>
        public static Outcome Restore(string install, Backup b)
        {
            var (version, commit) = VersionOf(install);
            var o = new Outcome { Kind = Compare(b, version, commit) };
            var files = Path.Combine(b.Folder, "files");
            var replaced = Path.Combine(b.Folder, "replaced-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            foreach (var f in Directory.Exists(files) ? Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
            {
                var rel = Rel(files, f);
                if (!Allowed(rel, o.Kind))
                {
                    var part = PartOf(rel);
                    if (!o.LeftOut.Contains(part)) o.LeftOut.Add(part);
                    continue;
                }
                var to = Path.Combine(install, Os(rel));
                if (File.Exists(to))
                {
                    Copy(to, Path.Combine(replaced, Os(rel)));
                    o.Replaced = replaced;
                }
                Copy(f, to);
                o.Restored++;
            }
            return o;
        }

        /// <summary>Whether a backed-up file goes back into an installation of that kind: a part's
        /// own rule; the mods folder's own files (the mods' settings) always.</summary>
        static bool Allowed(string rel, Kind kind)
        {
            foreach (var (part, into) in Parts)
                if (part.EndsWith("/") ? rel.StartsWith(part, StringComparison.OrdinalIgnoreCase) : rel.Equals(part, StringComparison.OrdinalIgnoreCase))
                    return into.Contains(kind);
            return rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && rel.IndexOf('/', 5) < 0;
        }

        static string PartOf(string rel) =>
            Parts.Select(p => p.Path).FirstOrDefault(p => p.EndsWith("/") ? rel.StartsWith(p, StringComparison.OrdinalIgnoreCase) : rel.Equals(p, StringComparison.OrdinalIgnoreCase)) ?? rel;

        /// <summary>A copy that never follows a hard link back: the backup must outlive the
        /// installation's files, which the uninstall deletes and HellForge may write in place.</summary>
        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, true);
        }

        static string Os(string rel) => rel.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar);

        static string Rel(string root, string file) =>
            file.Substring(Path.GetFullPath(root).TrimEnd('\\').Length + 1).Replace('\\', '/');
    }
}
