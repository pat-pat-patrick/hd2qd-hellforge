using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Qd.HellForge
{
    /// <summary>HellForge (SkyeShade/HellForge) from HD2 Mod Quick Deploy: an offline Helldivers 2 of
    /// its own on game build 25480438. This plugin is a launcher of HellForge's as its docs describe
    /// one (app\sdk\docs\external-launchers.md): it starts and stops sessions, picks the profile,
    /// changes the settings a player needs, shows the logs, and manages the mods in HellForge's
    /// mods\ folder, all through HellForge.Cli.exe and the files those docs name. It puts library
    /// mods into that folder (hard links), marks code mods "only affects me", and notes on a library
    /// mod's row what is known to go wrong on HellForge's older game.
    ///
    /// HellForge.exe is still needed for the first setup and repairs (copying the game, Steam's
    /// ownership check, the network block) and for syncing the account (Sync Inventory has no
    /// command yet; upstream issue #333).
    ///
    /// A window plugin of HD2 Mod Quick Deploy, called by name (core\Plugins.cs, PluginFiles):
    /// every method takes and gives strings, numbers, bools, delegates and Json shapes only.</summary>
    public static partial class Plugin
    {
        public const int Api = 2;
        public const string Kind = "window";

        public static string Title(string lang) => Words.Get(lang, "title");

        public static string Summary(string lang) => Words.Get(lang, "summary");

        static string Home(Dictionary<string, object> host) => (string)host["home"];

        // ---------------------------------------------------------------- the installation

        static string ConfigFile(string home) => Path.Combine(home, "hellforge", "config.json");

        /// <summary>The installation the player chose, or null.</summary>
        internal static string Chosen(string home)
        {
            try
            {
                var p = ConfigFile(home);
                return File.Exists(p) ? Json.Str(Json.Obj(File.ReadAllText(p, Encoding.UTF8)), "install") : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        internal static void Choose(string home, string install) =>
            Placed.WriteText(ConfigFile(home), Json.Write(new Dictionary<string, object> { ["install"] = install }));

        static string found;
        static DateTime foundAt;

        /// <summary>The installation (looked for again at most every 30 s), or null.</summary>
        internal static string Found(string home, bool again = false)
        {
            lock (typeof(Plugin))
            {
                if (!again && found != null && Install.IsInstall(found) && (DateTime.UtcNow - foundAt).TotalSeconds < 30) return found;
                if (!again && found == null && (DateTime.UtcNow - foundAt).TotalSeconds < 30) return null;
                found = Install.Find(Chosen(home));
                foundAt = DateTime.UtcNow;
                return found;
            }
        }

        /// <summary>The library's mods, each (name, path, enabled).</summary>
        internal static List<(string Name, string Path, bool Enabled)> Library(Dictionary<string, object> host) =>
            host.TryGetValue("library", out var f) && f is Func<List<Dictionary<string, object>>> lib
                ? lib().Select(m => ((string)m["name"], (string)m["path"], m.TryGetValue("enabled", out var e) && e is bool b && b)).ToList()
                : new List<(string, string, bool)>();

        // ---------------------------------------------------------------- start: keep HellForge's copies current

        /// <summary>Once in the background when the window starts: placed mods whose library files
        /// changed are placed again, and mods marked "only affects me" that a new version brought
        /// without the mark get it again. Nothing is changed while a session runs (HellForge
        /// refuses mod changes then too). True when something changed.</summary>
        public static bool Start(Dictionary<string, object> host)
        {
            var home = Home(host);
            var install = Found(home, true);
            if (install == null) return false;
            try { return Sync(home, install).Count > 0; }
            catch (Exception e) when (e is HfError || e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>What Start does; returns what it changed, worded in English for the log.</summary>
        internal static List<string> Sync(string home, string install)
        {
            var said = new List<string>();
            var status = Hf.ReadStatus(install);
            if (status == null || status.Running || !status.GameCopy) return said;
            Placed.Forget(home, install);
            foreach (var e in Placed.Load(home, install).Values.Where(Placed.Stale).ToList())
            {
                Placed.Renew(home, install, e);
                said.Add("placed again: " + e.Name);
            }
            var list = Hf.ReadMods(install);
            foreach (var n in Coop.Reapply(home, install, list)) said.Add("only affects me again: " + n);
            notesAt = DateTime.MinValue;
            return said;
        }

        // ---------------------------------------------------------------- notes on the library's rows

        static Dictionary<string, Placed.Entry> notesPlaced;
        static DateTime notesAt;
        static string notesInstall;

        /// <summary>On a library mod that is in HellForge: that it is there, or what is known to go
        /// wrong with it on HellForge's older game. Nothing on mods not put into HellForge.</summary>
        public static Dictionary<string, object> ModNote(string modFolder, Dictionary<string, object> host)
        {
            var home = Home(host);
            var install = Found(home);
            if (install == null) return null;
            Dictionary<string, Placed.Entry> mine;
            lock (typeof(Plugin))
            {
                if (notesPlaced == null || notesInstall != install || (DateTime.UtcNow - notesAt).TotalSeconds > 5)
                {
                    notesPlaced = Placed.Load(home, install);
                    notesInstall = install;
                    notesAt = DateTime.UtcNow;
                }
                mine = notesPlaced;
            }
            var entry = Placed.Of(mine, modFolder);
            if (entry == null) return null;
            var lang = Words.Lang(host);
            var facts = Scan.Of(modFolder);
            if (facts.Any)
                return new Dictionary<string, object>
                {
                    ["text"] = Words.Get(lang, "note_risk"), ["tip"] = string.Join("\n", Risks(lang, facts)), ["colour"] = "Yellow",
                };
            if (Placed.Stale(entry))
                return new Dictionary<string, object> { ["text"] = Words.Get(lang, "note_stale"), ["tip"] = Words.Get(lang, "note_stale_tip"), ["colour"] = "Yellow" };
            return new Dictionary<string, object> { ["text"] = Words.Get(lang, "note_in"), ["tip"] = Words.Get(lang, "note_in_tip"), ["colour"] = "Blue" };
        }

        /// <summary>Each known risk of a mod on HellForge, worded.</summary>
        internal static List<string> Risks(string lang, Scan.Facts f)
        {
            var r = new List<string>();
            if (f.RenderConfig) r.Add(Words.Get(lang, "risk_render_config"));
            if (f.Strings) r.Add(Words.Get(lang, "risk_strings"));
            if (f.BootModLoaderApi) r.Add(Words.Get(lang, "risk_bml"));
            return r;
        }

        // ---------------------------------------------------------------- command line (English, as the manager's own)

        public static readonly string[][] Commands =
        {
            new[] { "hellforge", "show the HellForge installation, its session and the mods in its mods folder" },
            new[] { "hellforge-add", "hellforge-add MOD...: put library mods into HellForge's mods folder (hard links on the same drive)" },
            new[] { "hellforge-remove", "hellforge-remove MOD...: take mods this plugin put into HellForge out again (the library is not touched)" },
            new[] { "hellforge-only-me", "hellforge-only-me MOD on|off: a HellForge code mod counted as each player's own in co-op (\"coop\": \"per-player\")" },
            new[] { "hellforge-play", "start a HellForge session with its launcher's settings" },
            new[] { "hellforge-stop", "hellforge-stop [--now]: end the running HellForge session" },
        };

        public static int Run(string command, string[] args, TextWriter o, Dictionary<string, object> host)
        {
            var home = Home(host);
            var install = Found(home, true) ?? throw new HfError("no HellForge installation found (a folder with hellforge-install.json)");
            switch (command)
            {
                case "hellforge":
                {
                    var s = Hf.ReadStatus(install);
                    o.WriteLine("HellForge " + s.HellForge + ", engine " + s.Engine + ", mod SDK " + s.ModSdk + ", game build " + s.GameBuild + ": " + install);
                    o.WriteLine(!s.GameCopy ? "not set up yet: start HellForge.exe once" : s.Running ? "a session runs: " + s.LastRun : "no session runs");
                    var list = Hf.ReadMods(install);
                    var mine = Placed.Load(home, install);
                    o.WriteLine("modlist code: " + (list.Modlist ?? "none (no gameplay mod is on)"));
                    foreach (var m in list.Mods.Where(m => !m.Hidden))
                        o.WriteLine("  " + (m.Enabled ? "on " : "off") + "  " + m.Group.PadRight(10) + " " + m.Name + (m.Version != "" ? " " + m.Version : "") +
                                    (mine.Values.Any(e => string.Equals(e.Id, m.Id, StringComparison.OrdinalIgnoreCase)) ? "  [from the library]" : ""));
                    foreach (var p in list.Problems) o.WriteLine("problem: " + p);
                    return 0;
                }
                case "hellforge-add":
                case "hellforge-remove":
                {
                    if (args.Length == 0) return 2;
                    NotRunning(install);
                    var lib = Library(host);
                    var list = Hf.ReadMods(install);
                    int refused = 0;
                    foreach (var a in args)
                    {
                        var m = Find(lib, a);
                        try
                        {
                            if (command == "hellforge-add")
                            {
                                var e = Placed.Put(home, install, m.Name, m.Path, list);
                                o.WriteLine(m.Name + ": in HellForge as " + e.Folder + (e.Linked ? " (hard links)" : " (copied: another drive)"));
                                var risks = Risks("en", Scan.Of(m.Path));
                                foreach (var r in risks) o.WriteLine("  " + r);
                                list = Hf.ReadMods(install);
                            }
                            else
                            {
                                var e = Placed.Of(Placed.Load(home, install), m.Path) ?? throw new HfError("not put into HellForge by this plugin");
                                Placed.Remove(home, install, e);
                                o.WriteLine(m.Name + ": taken out of HellForge");
                            }
                        }
                        catch (HfError e) { o.WriteLine(m.Name + ": refused: " + Refused("en", e)); refused++; }
                    }
                    return refused > 0 ? 1 : 0;
                }
                case "hellforge-only-me":
                {
                    if (args.Length != 2 || (args[1] != "on" && args[1] != "off")) return 2;
                    NotRunning(install);
                    var list = Hf.ReadMods(install);
                    var m = list.Mods.Where(x => Coop.Applies(x) && (string.Equals(x.Id, args[0], StringComparison.OrdinalIgnoreCase) || x.Name.IndexOf(args[0], StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                    if (m.Count != 1) throw new HfError(m.Count == 0 ? "no HellForge code mod is named " + args[0] : args[0] + " names " + m.Count + " mods");
                    Coop.Set(home, install, m[0], args[1] == "on");
                    var after = Hf.ReadMods(install).Mods.FirstOrDefault(x => x.Id == m[0].Id);
                    o.WriteLine(m[0].Name + ": " + (after?.Group ?? "?"));
                    return 0;
                }
                case "hellforge-play":
                {
                    var r = Hf.Run(install, "play");
                    o.WriteLine(r.Ok ? r.Out.Trim() : "refused: " + r.Why);
                    return r.Ok ? 0 : 1;
                }
                case "hellforge-stop":
                {
                    var r = args.Contains("--now") ? Hf.Run(install, "stop", "--now", "yes") : Hf.Run(install, "stop");
                    o.WriteLine(r.Ok ? "the session is ending" : "refused: " + r.Why);
                    return r.Ok ? 0 : 1;
                }
            }
            return 2;
        }

        static void NotRunning(string install)
        {
            var s = Hf.ReadStatus(install);
            if (s.Running) throw new HfError("a HellForge session runs: HellForge keeps the mods it started with; change them after it ends");
            if (!s.GameCopy) throw new HfError("HellForge is not set up yet: start HellForge.exe once");
        }

        /// <summary>A refusal in the language.</summary>
        internal static string Refused(string lang, HfError e) =>
            e is Placed.Refusal r ? Words.F(lang, "refuse_" + r.Key, r.Arg ?? "") : e.Message;

        static (string Name, string Path, bool Enabled) Find(List<(string Name, string Path, bool Enabled)> lib, string text)
        {
            var exact = lib.Where(m => string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var some = lib.Where(m => m.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (some.Count == 1) return some[0];
            throw new HfError(some.Count == 0 ? "no mod in the library is named " + text : text + " names " + some.Count + " mods: " + string.Join(", ", some.Select(m => m.Name)));
        }
    }
}
