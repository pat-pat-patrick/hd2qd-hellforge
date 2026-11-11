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

        /// <summary>The title-bar button's icon: HellForge's own, read from the player's
        /// HellForge.exe (ExeIcon says why it is not shipped); null when HellForge is not found or
        /// has none, and the manager draws the anvil this plugin ships (app.ico).</summary>
        public static byte[] Icon(Dictionary<string, object> host)
        {
            var install = Found(Home(host));
            return install == null ? null : ExeIcon.Read(Install.Launcher(install));
        }

        /// <summary>The official game's data folder (the manager's game folder), or null.</summary>
        internal static string Data(Dictionary<string, object> host) => host.TryGetValue("data", out var d) ? d as string : null;

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
            try { return Sync(home, install, Data(host)).Count > 0; }
            catch (Exception e) when (e is HfError || e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>What Start does; returns what it changed, worded in English for the log.</summary>
        internal static List<string> Sync(string home, string install, string officialData = null)
        {
            var said = new List<string>();
            var status = Hf.ReadStatus(install);
            if (status == null || status.Running || !status.GameCopy) return said;
            Placed.Forget(home, install);
            foreach (var e in Placed.Load(home, install).Values.Where(x => Placed.Stale(x) || Placed.GamesChanged(x, install, officialData)).ToList())
            {
                Placed.Renew(home, install, e, officialData);
                said.Add("placed again: " + e.Name);
            }
            var list = Hf.ReadMods(install);
            foreach (var n in Coop.Reapply(home, install, list)) said.Add("only affects me again: " + n);
            var all = Coop.MarkAll(home, install);
            foreach (var n in all.Marked) said.Add("only affects me (every gameplay mod): " + n);
            foreach (var n in all.Refused) said.Add("HellForge keeps it shared: " + n);
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
            var lang = Words.Lang(host);
            // on the manager's HellForge target: what each deployed mod became there
            if (host.TryGetValue("target", out var tf) && tf is Func<string> target && target() == "hellforge")
            {
                var dep = Deployed.Load(home, install).Values.FirstOrDefault(d => Placed.SamePath(d.Library, modFolder));
                if (dep == null) return null;
                var lines = PortLines(lang, dep.Level, dep.Merged, dep.LeftOutTypes, dep.KeptDiffering);
                var bml = Risks(lang, Scan.Of(modFolder), true);
                if (lines.Count == 0 && bml.Count == 0) return null;
                return new Dictionary<string, object>
                {
                    ["text"] = Words.Get(lang, dep.Level == Port.Level.Partial.ToString() ? "port_note_partial" : bml.Count > 0 ? "note_risk" : "port_note_adjusted"),
                    ["tip"] = string.Join("\n", lines.Select(l => l.Text).Concat(bml)), ["colour"] = lines.Any(l => l.Colour == "Yellow") || bml.Count > 0 ? "Yellow" : "Blue",
                };
            }
            var entry = Placed.Of(mine, modFolder);
            if (entry == null) return null;
            bool ported = entry.Level != null && entry.Level != Port.Level.Unknown.ToString();
            var risks = Risks(lang, Scan.Of(modFolder), ported);
            var port = PortLines(lang, entry);
            if (risks.Count > 0 || entry.Level == Port.Level.Partial.ToString() || entry.Level == Port.Level.Adjusted.ToString())
                return new Dictionary<string, object>
                {
                    ["text"] = Words.Get(lang, entry.Level == Port.Level.Partial.ToString() ? "port_note_partial" : risks.Count > 0 ? "note_risk" : "port_note_adjusted"),
                    ["tip"] = string.Join("\n", port.Select(p => p.Text).Concat(risks)), ["colour"] = "Yellow",
                };
            if (Placed.Stale(entry))
                return new Dictionary<string, object> { ["text"] = Words.Get(lang, "note_stale"), ["tip"] = Words.Get(lang, "note_stale_tip"), ["colour"] = "Yellow" };
            return new Dictionary<string, object> { ["text"] = Words.Get(lang, "note_in"), ["tip"] = Words.Get(lang, "note_in_tip"), ["colour"] = "Blue" };
        }

        /// <summary>Each known risk of a mod on HellForge, worded. With ported (the two games were
        /// compared: Port), only what the comparison cannot see: Lua that calls Boot Mod Loader's API.</summary>
        internal static List<string> Risks(string lang, Scan.Facts f, bool ported = false)
        {
            var r = new List<string>();
            if (f.RenderConfig && !ported) r.Add(Words.Get(lang, "risk_render_config"));
            if (f.Strings && !ported) r.Add(Words.Get(lang, "risk_strings"));
            if (f.BootModLoaderApi) r.Add(Words.Get(lang, "risk_bml"));
            return r;
        }

        internal static string TypeName(string lang, string hex) =>
            Words.Get(lang, Port.TypeKey(ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var t) ? t : 0));

        static string Counted(string lang, Dictionary<string, int> byType) =>
            string.Join(Words.Get(lang, "list_join"), byType.OrderByDescending(kv => kv.Value).Select(kv => TypeName(lang, kv.Key) + " " + kv.Value));

        /// <summary>What HellForge's copy of a mod is, as lines and colours (none: as it is).</summary>
        internal static List<(string Text, string Colour)> PortLines(string lang, string level, int merged, Dictionary<string, int> leftOut, Dictionary<string, int> kept)
        {
            var lines = new List<(string, string)>();
            if (level == null) return lines;
            if (level == Port.Level.Unknown.ToString()) { lines.Add((Words.Get(lang, "port_unknown"), "Muted")); return lines; }
            if (leftOut != null && leftOut.Count > 0) lines.Add((Words.F(lang, "port_left_out", Counted(lang, leftOut)), "Yellow"));
            if (merged > 0) lines.Add((Words.F(lang, "port_merged", merged), "Blue"));
            if (kept != null && kept.Count > 0) lines.Add((Words.F(lang, "port_kept", Counted(lang, kept)), "Yellow"));
            return lines;
        }

        internal static List<(string Text, string Colour)> PortLines(string lang, Port.Report r) =>
            r == null ? new List<(string, string)>() : PortLines(lang, r.Level.ToString(), r.Merged,
                r.LeftOutTypes.ToDictionary(k => k.Key.ToString("x16"), k => k.Value), r.KeptDiffering.ToDictionary(k => k.Key.ToString("x16"), k => k.Value));

        internal static List<(string Text, string Colour)> PortLines(string lang, Placed.Entry e) =>
            PortLines(lang, e.Level, e.Merged, e.LeftOutTypes, e.KeptDiffering);

        // ---------------------------------------------------------------- command line (English, as the manager's own)

        public static readonly string[][] Commands =
        {
            new[] { "hellforge", "show the HellForge installation, its session and the mods in its mods folder" },
            new[] { "hellforge-add", "hellforge-add MOD...: put library mods into HellForge's mods folder (hard links on the same drive)" },
            new[] { "hellforge-remove", "hellforge-remove MOD...: take mods this plugin put into HellForge out again (the library is not touched)" },
            new[] { "hellforge-only-me", "hellforge-only-me MOD|--all on|off: a HellForge code mod (--all: every shared gameplay code mod, now and later) counted as each player's own in co-op (\"coop\": \"per-player\")" },
            new[] { "hellforge-play", "start a HellForge session with its launcher's settings" },
            new[] { "hellforge-stop", "hellforge-stop [--now]: end the running HellForge session" },
        };

        public static int Run(string command, string[] args, TextWriter o, Dictionary<string, object> host)
        {
            var home = Home(host);
            var install = Found(home, true) ?? throw new HfError("No HellForge installation was found (a folder that contains hellforge-install.json).");
            switch (command)
            {
                case "hellforge":
                {
                    var s = Hf.ReadStatus(install);
                    o.WriteLine("HellForge " + s.HellForge + ", engine " + s.Engine + ", mod SDK " + s.ModSdk + ", game build " + s.GameBuild + ": " + install);
                    o.WriteLine(!s.GameCopy ? "not set up yet: start HellForge.exe once" : s.Running ? "a session runs: " + s.LastRun : "no session runs");
                    var list = Hf.ReadMods(install);
                    var mine = Placed.Load(home, install);
                    o.WriteLine("modlist code: " + (list.Modlist ?? "none (no shared gameplay mods are enabled)"));
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
                                var e = Placed.Put(home, install, m.Name, m.Path, list, Data(host));
                                o.WriteLine(m.Name + ": in HellForge as " + e.Folder + (e.Linked ? " (hard links)" : " (copied: another drive)") + ", " + e.Level +
                                            (e.Merged > 0 ? ", " + e.Merged + " text tables merged onto HellForge's" : "") + (e.LeftOut > 0 ? ", " + e.LeftOut + " render configurations left out" : "") +
                                            (e.KeptDiffering.Count > 0 ? ", kept although the games differ: " + string.Join(", ", e.KeptDiffering.Select(k => TypeName("en", k.Key) + " " + k.Value)) : ""));
                                foreach (var n in e.Notes) o.WriteLine("  " + n);
                                var risks = Risks("en", Scan.Of(m.Path));
                                foreach (var r in risks) o.WriteLine("  " + r);
                                list = Hf.ReadMods(install);
                            }
                            else
                            {
                                var e = Placed.Of(Placed.Load(home, install), m.Path) ?? throw new HfError("This mod was not added to HellForge by this plugin.");
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
                    if (args[0] == "--all")
                    {
                        var (marked, refusedAll) = Coop.SetAll(home, install, args[1] == "on");
                        o.WriteLine("all gameplay mods set to this player only: " + args[1]);
                        foreach (var n in marked) o.WriteLine("  marked: " + n);
                        foreach (var n in refusedAll) o.WriteLine("  HellForge requires it to remain shared: " + n);
                        return 0;
                    }
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
            if (s.Running) throw new HfError("A HellForge session is running. HellForge keeps the mods the session started with; they can be changed after it ends.");
            if (!s.GameCopy) throw new HfError("HellForge has not been set up. Run HellForge.exe once to complete the setup.");
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
