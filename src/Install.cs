using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Hd2Qd.HellForge
{
    /// <summary>Finding a HellForge installation, by the rules HellForge's docs give launchers
    /// (app\sdk\docs\external-launchers.md, "Finding an installation"), the same as HD2 Mod
    /// Setting GUI's: a folder with hellforge-install.json (bts-install.json before the rename).
    /// HellForge writes nothing to the registry.</summary>
    static class Install
    {
        public static readonly string[] Markers = { "hellforge-install.json", "bts-install.json" };

        public static bool IsInstall(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && Markers.Any(m => File.Exists(Path.Combine(dir, m))); }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException || e is NotSupportedException) { return false; }
        }

        /// <summary>Tests only: the places looked in, instead of the real ones.</summary>
        public static Func<IEnumerable<string>> PlacesOverride;

        /// <summary>The installation: the one chosen (when it still is one), else where the installer
        /// puts it (Documents\HellForge; %USERPROFILE%\HellForge when OneDrive holds Documents), else
        /// where a HellForge.lnk on the desktop or in the Start menu points, else the one a HellForge
        /// program that runs is in. null: none found.</summary>
        public static string Find(string chosen)
        {
            if (IsInstall(chosen)) return Path.GetFullPath(chosen);
            var places = PlacesOverride != null ? PlacesOverride() : DefaultPlaces().Concat(FromShortcuts());
            foreach (var dir in places)
                if (IsInstall(dir)) return Path.GetFullPath(dir);
            return PlacesOverride != null ? null : FromRunning();
        }

        /// <summary>The installation of a HellForge program that runs (its launcher or its session;
        /// never the game, whose process is not opened). null: none runs.</summary>
        public static string FromRunning()
        {
            foreach (var name in new[] { "HellForge.Launcher", "HellForge", "HellForge.Session" })
                foreach (var exe in ExesOf(name))
                {
                    var found = InstallAbove(exe);
                    if (found != null) return found;
                }
            return null;
        }

        /// <summary>The nearest folder above a file that is an installation (up to 6 up).</summary>
        public static string InstallAbove(string file)
        {
            try
            {
                var dir = string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(file);
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++, dir = Path.GetDirectoryName(dir))
                    if (IsInstall(dir)) return dir;
            }
            catch (Exception e) when (e is ArgumentException || e is PathTooLongException) { }
            return null;
        }

        static IEnumerable<string> DefaultPlaces()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home != "") yield return Path.Combine(home, "Documents", "HellForge");
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (docs != "") yield return Path.Combine(docs, "HellForge");
            if (home != "") yield return Path.Combine(home, "HellForge");
        }

        static IEnumerable<string> FromShortcuts()
        {
            var places = new[]
            {
                Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms,
            };
            foreach (var sf in places)
            {
                var dir = Environment.GetFolderPath(sf);
                if (dir == "") continue;
                var links = new List<string>();
                try
                {
                    links.AddRange(Directory.GetFiles(dir, "HellForge.lnk"));
                    foreach (var sub in Directory.GetDirectories(dir)) links.AddRange(Directory.GetFiles(sub, "HellForge.lnk"));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                foreach (var link in links)
                {
                    var target = LinkTarget(link);
                    if (target != null) yield return Path.GetDirectoryName(target);
                }
            }
        }

        // a shortcut's target, read by Windows's own shell object; null when it cannot be read
        static string LinkTarget(string link)
        {
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return null;
                var shell = Activator.CreateInstance(type);
                var sc = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The program files of the processes of that name that can be read (a limited
        /// query: no rights beyond the player's own).</summary>
        static IEnumerable<string> ExesOf(string name)
        {
            Process[] all;
            try { all = Process.GetProcessesByName(name); }
            catch (Exception) { yield break; }
            foreach (var p in all)
            {
                string path = null;
                try
                {
                    var h = OpenProcess(0x1000, false, p.Id);          // PROCESS_QUERY_LIMITED_INFORMATION
                    if (h != IntPtr.Zero)
                        try
                        {
                            var sb = new StringBuilder(1024);
                            int n = sb.Capacity;
                            if (QueryFullProcessImageName(h, 0, sb, ref n)) path = sb.ToString(0, n);
                        }
                        finally { CloseHandle(h); }
                }
                catch (Exception) { }
                finally { p.Dispose(); }
                if (path != null) yield return path;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        // ---- the installation's folders (external-launchers.md, "Finding an installation") ----

        public static string Cli(string install) => Path.Combine(install, "app", "cli", "HellForge.Cli.exe");
        public static string Launcher(string install) => Path.Combine(install, "HellForge.exe");
        public static string Mods(string install) => Path.Combine(install, "mods");

        const string GameSettings = @"Arrowhead\Helldivers2";

        /// <summary>The game's roaming AppData when no sandboxed run uses a copy of it.</summary>
        public static string Roaming(string install) => Path.Combine(install, "userdata", "environment", "Roaming");

        /// <summary>A sandboxed run's copy of the roaming AppData (copied back when the game ends).</summary>
        public static string RunRoaming(string run) => Path.Combine(run, "game", "environment", "Roaming");

        /// <summary>Bingus Shared Loader's log: the running session's copy, else userdata's
        /// (checked 2026-11-11 on HellForge 0.4.0-beta).</summary>
        public static string BingusLog(string install, string run) =>
            Path.Combine(run != null ? Path.Combine(run, "game", "environment", "Local") : Path.Combine(install, "userdata", "environment", "Local"),
                         "CowboyBingus", "Helldivers2", "Logs", "BingusSharedLoader.log");

        /// <summary>The folder the game's mods read and write settings in now.</summary>
        public static string SettingsFolder(string install, Hf.Status s) =>
            Path.Combine(s != null && s.Running && s.Sandbox && !string.IsNullOrEmpty(s.LastRun) ? RunRoaming(s.LastRun) : Roaming(install), GameSettings);
    }
}
