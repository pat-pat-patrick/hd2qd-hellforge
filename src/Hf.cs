using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hd2Qd.HellForge
{
    /// <summary>HellForge's command line, app\cli\HellForge.Cli.exe: the interface its docs
    /// promise launchers of their own (external-launchers.md, "The commands"). Every command
    /// prints JSON on standard output; a failure prints "HellForge: why" on standard error and
    /// exits with 1. Nothing here writes into the installation by itself: game\, app\, the
    /// backend and the network block are HellForge's alone.</summary>
    static class Hf
    {
        public sealed class Result
        {
            public int Code;
            public string Out = "", Err = "";
            public bool Ok => Code == 0;

            /// <summary>Why it failed, as HellForge said it ("HellForge: " left out).</summary>
            public string Why
            {
                get
                {
                    var e = (Err ?? "").Trim();
                    if (e.StartsWith("HellForge:")) e = e.Substring(10).Trim();
                    return e.Length > 0 ? e : "exit code " + Code;
                }
            }
        }

        /// <summary>Tests only: runs a command instead of the installation's program.</summary>
        public static Func<string, string[], Result> Runner = RunProgram;

        /// <summary>Tests only: runs a streaming command (play --wait yes) instead of the program;
        /// each line goes to the action; the task ends with the exit code.</summary>
        public static Func<string, string[], Action<string>, CancellationToken, Task<int>> Streamer = StreamProgram;

        public static Result Run(string install, params string[] args) => Runner(install, args);

        /// <summary>A command's JSON object; throws with HellForge's reason when it failed.</summary>
        public static Dictionary<string, object> Ask(string install, params string[] args)
        {
            var r = Run(install, args);
            if (!r.Ok) throw new HfError(r.Why);
            return Json.Obj(r.Out) ?? throw new HfError("HellForge.Cli " + args.FirstOrDefault() + ": no JSON");
        }

        static Result RunProgram(string install, string[] args)
        {
            var exe = Install.Cli(install);
            if (!File.Exists(exe)) return new Result { Code = -1, Err = "HellForge: " + exe + " is missing" };
            var psi = new ProcessStartInfo(exe, Quote(args))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = install,
            };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                // a build of source mods may download Zig first (100 MB, 2.5 minutes on 2026-11-12)
                bool build = args.Length > 0 && args[0] == "build-source-mods";
                if (!p.WaitForExit(build ? 30 * 60000 : 120000))
                {
                    try { p.Kill(); } catch (Exception) { }
                    return new Result { Code = -1, Err = "HellForge: the command did not finish in " + (build ? "30" : "2") + " minutes" };
                }
                return new Result { Code = p.ExitCode, Out = o.Result, Err = e.Result };
            }
        }

        static Task<int> StreamProgram(string install, string[] args, Action<string> line, CancellationToken stop)
        {
            var exe = Install.Cli(install);
            var psi = new ProcessStartInfo(exe, Quote(args))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = install,
            };
            return Task.Run(() =>
            {
                using (var p = Process.Start(psi))
                {
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    string l;
                    // closing play does not stop the session (the docs): a cancel only stops listening
                    using (stop.Register(() => { try { p.Kill(); } catch (Exception) { } }))
                        while ((l = p.StandardOutput.ReadLine()) != null) line(l);
                    p.WaitForExit();
                    if (p.ExitCode != 0 && err.Length > 0) line(Json.Write(new Dictionary<string, object> { ["event"] = "error", ["why"] = new Result { Code = p.ExitCode, Err = err.ToString() }.Why }));
                    return p.ExitCode;
                }
            });
        }

        /// <summary>A command line from arguments, as Windows programs split it again
        /// (CommandLineToArgvW's rules: quotes around anything with a space or a quote,
        /// backslashes doubled before a quote).</summary>
        public static string Quote(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteOne));

        static string QuoteOne(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (var c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); slashes = 0; continue; }
                sb.Append('\\', slashes); slashes = 0; sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        // ---------------------------------------------------------------- status

        /// <summary>What status says (the fields used).</summary>
        public sealed class Status
        {
            public string HellForge, Engine, GameBuild;
            public long ModSdk;
            public bool GameCopy, SessionRunner;
            public bool Running, Sandbox;
            /// <summary>The folder of the run going on, or of the last one.</summary>
            public string LastRun, LastStatus, LastFailure;
            public string Profile = "default";
        }

        public static Status ParseStatus(Dictionary<string, object> root)
        {
            if (root == null) return null;
            var ready = Json.Obj(root, "ready");
            var session = Json.Obj(root, "session");
            var settings = Json.Obj(root, "settings");
            return new Status
            {
                HellForge = Json.Str(root, "hellforge"), Engine = Json.Str(root, "engine"), GameBuild = Json.Str(root, "gameBuild"),
                ModSdk = Json.Long(root, "modSdk"),
                GameCopy = Json.Bool(ready, "gameCopy"), SessionRunner = Json.Bool(ready, "sessionRunner"),
                Running = Json.Bool(session, "running"), LastRun = Json.Str(session, "lastRun"),
                LastStatus = Json.Str(session, "lastStatus"), LastFailure = Json.Str(session, "lastFailure"),
                Sandbox = Json.Bool(settings, "sandbox"),
                Profile = Json.Str(settings, "profile") is string p && p != "" ? p : "default",
            };
        }

        public static Status ReadStatus(string install) => ParseStatus(Ask(install, "status"));

        // ---------------------------------------------------------------- mods

        public sealed class Option
        {
            public string Name;
            public bool On;
            public List<string> Variants = new List<string>();
            public string Picked;
        }

        public sealed class Mod
        {
            public string Id, Name, Version, Group, Folder, Archive;
            public bool Enabled, Removable;
            public List<string> Tags = new List<string>();
            public List<Option> Options = new List<Option>();
            public bool Has(string tag) => Tags.Contains(tag);
            /// <summary>Local Stats: the in-game editor's saved changes; HellForge's Mods page hides it.</summary>
            public bool Hidden => Has("local-stats");
        }

        public sealed class ModList
        {
            public List<Mod> Mods = new List<Mod>();
            public List<string> Problems = new List<string>();
            /// <summary>The code friends compare; null when no gameplay mod is on.</summary>
            public string Modlist;
            public string SettingsFile;
        }

        public static ModList ParseMods(Dictionary<string, object> root)
        {
            var list = new ModList { Modlist = Json.Str(root, "modlist"), SettingsFile = Json.Str(root, "settings") };
            foreach (var p in Json.List(root, "problems"))
                list.Problems.Add(p is string s ? s : p is Dictionary<string, object> d ? (Json.Str(d, "message") ?? Json.Str(d, "why") ?? Json.Write(d)) : Convert.ToString(p));
            foreach (var m in Json.Objs(root, "mods"))
            {
                var mod = new Mod
                {
                    Id = Json.Str(m, "id"), Name = Json.Str(m, "name"), Version = Json.Str(m, "version") ?? "",
                    Group = Json.Str(m, "group"), Folder = Json.Str(m, "folder"), Archive = Json.Str(m, "archive"),
                    Enabled = Json.Bool(m, "enabled"), Removable = Json.Bool(m, "removable"), Tags = Json.Strs(m, "tags"),
                };
                foreach (var o in Json.Objs(m, "options"))
                    mod.Options.Add(new Option { Name = Json.Str(o, "name"), On = Json.Bool(o, "on"), Variants = Json.Strs(o, "variants"), Picked = Json.Str(o, "picked") });
                list.Mods.Add(mod);
            }
            return list;
        }

        public static ModList ReadMods(string install) => ParseMods(Ask(install, "mods"));

        // ---------------------------------------------------------------- profiles and settings

        public sealed class Profile
        {
            public string Name, Template;
            public bool Selected;
        }

        public static (List<Profile> Profiles, List<string> Templates) ParseProfiles(Dictionary<string, object> root) =>
            (Json.Objs(root, "profiles").Select(p => new Profile { Name = Json.Str(p, "name"), Template = Json.Str(p, "template"), Selected = Json.Bool(p, "selected") }).ToList(),
             Json.Objs(root, "templates").Select(t => Json.Str(t, "name")).Where(n => n != null).ToList());

        /// <summary>A profile's name as HellForge takes it: 1 to 32 lowercase letters, digits or hyphens.</summary>
        public static bool GoodProfileName(string name) =>
            !string.IsNullOrEmpty(name) && name.Length <= 32 && name.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');

        public sealed class Settings
        {
            public string Profile, War;
            public bool Multiplayer, Sandbox, Strangers, LiveChanges, SteamBridge;
            public bool? StrangersChosen;
            public long ForgeCredits = 30;
        }

        public static Settings ParseSettings(Dictionary<string, object> root) => new Settings
        {
            Profile = Json.Str(root, "profile"), War = Json.Str(root, "war") ?? "live",
            Multiplayer = Json.Bool(root, "multiplayer"), Sandbox = Json.Bool(root, "sandbox"), SteamBridge = Json.Bool(root, "steamBridge"),
            Strangers = Json.Bool(root, "playWithStrangers"), StrangersChosen = Json.MaybeBool(root, "playWithStrangersChosen"),
            LiveChanges = Json.Bool(root, "liveChanges"), ForgeCredits = Json.Long(root, "forgeCreditChance", 30),
        };

        // ---------------------------------------------------------------- sessions and logs

        public sealed class Session
        {
            public string Id, Folder, Status, Failure, ExitCode, Started, Completed;
            public bool Crashed;
        }

        public static List<Session> ParseRuns(Dictionary<string, object> root) =>
            Json.Objs(root, "runs").Select(r => new Session
            {
                Id = Json.Str(r, "id"), Folder = Json.Str(r, "folder"), Status = Json.Str(r, "status"), Failure = Json.Str(r, "failure"),
                ExitCode = Json.Str(r, "gameExitCode"), Started = Json.Str(r, "startedUtc"), Completed = Json.Str(r, "completedUtc"),
                Crashed = Json.Bool(r, "crashed"),
            }).ToList();

        /// <summary>How a session ended, for the player. HellForge's "crashed" also covers a game
        /// that wrote a crash dump while it quit as the player asked: on 25480438 quitting writes
        /// dumps and exits with 1 (checked 2026-11-11, three sessions of 2026-11-10, no loaderCrash
        /// nor windowsCrash). So "crash" only when the mod loader or Windows recorded one; a dump
        /// and an exit code alone are "ended with an error".</summary>
        public enum Ending { Running, Ended, EndedWithError, Crashed, Failed }

        public static Ending EndingOf(Session run, Dictionary<string, object> result)
        {
            if (run.Status == null || run.Completed == null && run.Status != "failed" && run.Status != "observed" && run.Status != "cleanup_failed") return Ending.Running;
            if (run.Status == "failed" || run.Status == "cleanup_failed") return Ending.Failed;
            bool recorded = result != null && (Has(result, "loaderCrash") || (result.TryGetValue("windowsCrash", out var w) && w is Dictionary<string, object>));
            if (recorded) return Ending.Crashed;
            if (run.Crashed || (run.ExitCode != null && run.ExitCode != "0x00000000" && run.ExitCode != "0")) return Ending.EndedWithError;
            return Ending.Ended;
        }

        static bool Has(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) && v != null && !(v is string s && (s == "" || s == "none"));

        /// <summary>A run's result.json, or null.</summary>
        public static Dictionary<string, object> ResultOf(string runFolder)
        {
            try
            {
                var p = Path.Combine(runFolder, "result.json");
                return File.Exists(p) ? Json.Obj(File.ReadAllText(p, Encoding.UTF8)) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>A refusal from HellForge, with its own words.</summary>
    class HfError : Exception
    {
        public HfError(string why) : base(why) { }
    }
}
