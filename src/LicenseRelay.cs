using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hd2Qd.HellForge
{
    /// <summary>(0.15.0, the user 2026-11-17: "接入授权") Licensed mods on HellForge. HellForge
    /// runs the game sandboxed with an AppData of its own: userdata\environment, copied into
    /// each session's folder (logs\sessions\&lt;id&gt;\game\environment) when it starts and back
    /// when it ends (HellForge's docs, steam-bridge.md). So a mod there reads no license of the
    /// player's, and its online question (HD2 Mod License spec section 7: licenses\&lt;issuer&gt;.ask)
    /// reaches no manager. This relays both ways, files only:
    ///   * the player's licenses (*.txt) and online answers (*.attest), from the real AppData
    ///     into userdata\environment (so a session starts with them) and into every session
    ///     folder that asked (so a renewal or an answer reaches the running game); one gone
    ///     from the real AppData (a sign-out) is deleted there too;
    ///   * a session's question, into the real AppData, where the Member licenses plugin
    ///     answers it; its answer then comes back by the first rule;
    ///   * member content (sealed segments, Member licenses 1.7.0, CONTRACT_sealed_lua section
    ///     12): that plugin decrypts licenses\&lt;issuer&gt;\segments\&lt;id&gt;\&lt;version&gt;\seg&lt;i&gt;.lua
    ///     in the real AppData when helldivers2.exe starts (HellForge's game is one too) and
    ///     deletes it when the game stops. The plaintext goes only into the running session's
    ///     folder and follows the real one away; it never goes into userdata\environment, and a
    ///     copy HellForge carries back there at a session's end is deleted. Ciphertext, receipts
    ///     and the key cache (seg*.enc, receipt.txt, key.dpapi) stay where they are.
    /// Each copy only when the bytes differ, through a temporary file. Three watchers for the
    /// process, made again only when AppData or the installation changes.</summary>
    static class LicenseRelay
    {
        const string Rel = @"Arrowhead\Helldivers2\licenses";
        static readonly object gate = new object();
        // every copy one at a time: two at once could read a file before and after a write and land
        // in the other order, the older bytes last (a question read empty, then written over the full one)
        static readonly object work = new object();
        static FileSystemWatcher real, sessions, sandbox;
        static string realDir, install, live;
        static DateTime liveMade;
        static readonly HashSet<string> asking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // <issuer>\segments\<id>\<version>\seg<i>.lua, relative to a licenses folder
        static readonly Regex Plain = new Regex(@"^[^\\]+\\segments\\[^\\]+\\[^\\]+\\seg\d+\.lua$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string RealDir(string appData) => Path.Combine(appData, Rel);
        internal static string SandboxDir(string install) => Path.Combine(install, "userdata", "environment", "Roaming", Rel);
        static string SessionsDir(string install) => Path.Combine(install, "logs", "sessions");
        static string LicensesOf(string sessionFolder) => Path.Combine(sessionFolder, "game", "environment", "Roaming", Rel);

        /// <summary>Starts relaying for this AppData and installation, and copies once now.</summary>
        internal static void Start(string appData, string inst)
        {
            if (string.IsNullOrEmpty(appData) || string.IsNullOrEmpty(inst)) return;
            lock (gate)
            {
                var r = RealDir(appData);
                if (real == null || !string.Equals(realDir, r, StringComparison.OrdinalIgnoreCase) || !string.Equals(install, inst, StringComparison.OrdinalIgnoreCase))
                {
                    real?.Dispose();
                    sessions?.Dispose();
                    sandbox?.Dispose();
                    real = sessions = sandbox = null;
                    realDir = r;
                    install = inst;
                    live = null;
                    liveMade = DateTime.MinValue;
                    asking.Clear();
                    Directory.CreateDirectory(realDir);
                    // the licenses and answers beside it, and the plaintext a game start decrypts below it
                    var w = new FileSystemWatcher(realDir) { IncludeSubdirectories = true, InternalBufferSize = 65536,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                    FileSystemEventHandler push = (s, e) => { if (Pushed(e.Name)) Later(PushAll); };
                    w.Created += push;
                    w.Changed += push;
                    w.Deleted += push;                       // a sign-out, or the game's end: gone in the sandbox and sessions too
                    w.Renamed += (s, e) => { if (Pushed(e.Name) || Pushed(e.OldName)) Later(PushAll); };
                    w.Error += (s, e) => Later(PushAll);
                    w.EnableRaisingEvents = true;
                    real = w;
                    // HellForge's copy of the plaintext back into userdata\environment at a session's end
                    var sb = SandboxDir(inst);
                    Directory.CreateDirectory(sb);
                    var x = new FileSystemWatcher(sb) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
                    FileSystemEventHandler clean = (s, e) => { if (IsPlain(e.Name)) Later(CleanSandbox); };
                    x.Created += clean;
                    x.Renamed += (s, e) => { if (IsPlain(e.Name)) Later(CleanSandbox); };
                    x.Error += (s, e) => Later(CleanSandbox);
                    x.EnableRaisingEvents = true;
                    sandbox = x;
                    var sd = SessionsDir(inst);
                    if (Directory.Exists(sd))
                    {
                        // the sessions of the last two days that asked: one may still run (the
                        // manager restarted during it) and so be told of a sign-out; an ended
                        // one is only HellForge's copy, which a file more or less leaves alone.
                        // The newest of them, if it started in the last twelve hours, may be the
                        // running game: it gets the plaintext while the real AppData holds it.
                        foreach (var dir in Directory.EnumerateDirectories(sd))
                        {
                            var lic = LicensesOf(dir);
                            if (!Directory.Exists(lic)) continue;
                            if (Directory.GetLastWriteTimeUtc(lic) > DateTime.UtcNow.AddDays(-2)) asking.Add(lic);
                            else if (HasPlain(lic)) asking.Add(lic);    // plaintext an earlier run left: taken away below
                            var made = Directory.GetCreationTimeUtc(dir);
                            if (made > DateTime.UtcNow.AddHours(-12) && made > liveMade) { liveMade = made; live = lic; }
                        }
                        // a session starting (HellForge copies the licenses folder in) and its questions
                        var q = new FileSystemWatcher(sd) { IncludeSubdirectories = true, InternalBufferSize = 65536,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                        q.Created += (s, e) => Seen(e.FullPath, true);
                        q.Changed += (s, e) => Seen(e.FullPath, false);
                        q.Renamed += (s, e) => Seen(e.FullPath, true);
                        q.Error += (s, e) => Later(PushAll);
                        q.EnableRaisingEvents = true;
                        sessions = q;
                    }
                }
            }
            lock (work) PushAll();
        }

        /// <summary>A file the real AppData's watcher relays: a license or an answer beside the
        /// question, or a segment's plaintext. Never a receipt, ciphertext or key cache.</summary>
        static bool Pushed(string name) =>
            name != null && (IsLicense(name) || IsPlain(name));

        static bool IsLicense(string name) =>
            name.IndexOf('\\') < 0 && (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".attest", StringComparison.OrdinalIgnoreCase));

        static bool IsPlain(string name) => name != null && Plain.IsMatch(name);

        static void Later(Action a) => ThreadPool.QueueUserWorkItem(_ => { try { lock (work) a(); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } });

        /// <summary>Something in logs\sessions. A question is relayed. Something created in a
        /// session folder made in the last ten minutes that has a licenses folder (HellForge
        /// starting a session copies it in) makes that session the running one, if no newer one
        /// is. Only a new session qualifies, so this relay's own writes into older ones never do.</summary>
        static void Seen(string path, bool created)
        {
            if (path == null) return;
            if (path.EndsWith(".ask", StringComparison.OrdinalIgnoreCase)) { Later(() => Relay(path)); return; }
            if (!created) return;
            string sd;
            lock (gate) sd = install == null ? null : SessionsDir(install);
            if (sd == null || path.Length <= sd.Length + 1 || !path.StartsWith(sd + "\\", StringComparison.OrdinalIgnoreCase)) return;
            var name = path.Substring(sd.Length + 1).Split('\\')[0];
            var folder = Path.Combine(sd, name);
            var lic = LicensesOf(folder);
            try
            {
                if (!Directory.Exists(lic)) return;
                var made = Directory.GetCreationTimeUtc(folder);
                if (made < DateTime.UtcNow.AddMinutes(-10)) return;
                lock (gate)
                {
                    if (realDir == null || string.Equals(lic, live, StringComparison.OrdinalIgnoreCase) || made < liveMade) return;
                    asking.Add(lic);
                    live = lic;
                    liveMade = made;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
            Later(PushAll);
        }

        /// <summary>The licenses and answers, into userdata\environment and every session that asked;
        /// the plaintext into the running session only, and away from every other.</summary>
        internal static void PushAll()
        {
            string from, inst, now;
            List<string> to;
            lock (gate)
            {
                from = realDir;
                inst = install;
                now = live;
                to = asking.ToList();
            }
            if (from == null) return;
            try { Push(from, SandboxDir(inst), false); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            foreach (var dir in to)
                try { Push(from, dir, string.Equals(dir, now, StringComparison.OrdinalIgnoreCase)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        static void CleanSandbox()
        {
            string inst;
            lock (gate) inst = install;
            if (inst != null) ClearPlain(SandboxDir(inst));
        }

        /// <summary>A session's question: that session gets the licenses now (it runs, so the
        /// plaintext too), and the question goes to the real AppData for the Member licenses
        /// plugin to answer.</summary>
        internal static void Relay(string askPath)
        {
            var dir = Path.GetDirectoryName(askPath);
            if (dir == null || !dir.EndsWith(Rel, StringComparison.OrdinalIgnoreCase)) return;
            string to;
            lock (gate)
            {
                if (realDir == null) return;
                asking.Add(dir);
                live = dir;
                try { liveMade = Directory.GetCreationTimeUtc(Path.GetFullPath(Path.Combine(dir, @"..\..\..\..\..\.."))); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { }
                to = realDir;
            }
            try
            {
                Push(to, dir, true);
                var text = Read(askPath);
                if (text != null) Copy(text, Path.Combine(to, Path.GetFileName(askPath)));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        /// <summary>Every *.txt and *.attest of `from` into `to`, where the bytes differ; and those
        /// `to` has that `from` has not, deleted (the user, 2026-11-17: signed out, and the beacon
        /// on HellForge stayed: the license copied in was never taken away). With `plain`, the
        /// segments' plaintext the same way; without, every plaintext in `to` is deleted. Files
        /// changed or deleted.</summary>
        internal static int Push(string from, string to, bool plain)
        {
            if (!Directory.Exists(from)) return 0;
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(from).Where(p => IsLicense(Path.GetFileName(p))))
            {
                var text = Read(f);
                if (text != null && Copy(text, Path.Combine(to, Path.GetFileName(f)))) n++;
            }
            if (plain)
                foreach (var rel in PlainOf(from))
                {
                    var text = Read(Path.Combine(from, rel));
                    if (text != null && Copy(text, Path.Combine(to, rel))) n++;
                }
            if (!Directory.Exists(to)) return n;
            foreach (var f in Directory.EnumerateFiles(to).Where(p => IsLicense(Path.GetFileName(p))).ToList())
                if (!File.Exists(Path.Combine(from, Path.GetFileName(f))))
                {
                    File.Delete(f);
                    n++;
                }
            foreach (var rel in PlainOf(to))
                if (!plain || !File.Exists(Path.Combine(from, rel)))
                {
                    File.Delete(Path.Combine(to, rel));
                    n++;
                }
            return n;
        }

        /// <summary>Deletes every segment plaintext in a licenses folder. Files deleted.</summary>
        internal static int ClearPlain(string dir)
        {
            int n = 0;
            foreach (var rel in PlainOf(dir))
                try { File.Delete(Path.Combine(dir, rel)); n++; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return n;
        }

        static bool HasPlain(string dir) => PlainOf(dir).Any();

        /// <summary>The plaintext segments under a licenses folder, as paths relative to it.</summary>
        static List<string> PlainOf(string dir)
        {
            var found = new List<string>();
            if (!Directory.Exists(dir)) return found;
            foreach (var iss in Directory.EnumerateDirectories(dir))
            {
                var seg = Path.Combine(iss, "segments");
                if (!Directory.Exists(seg)) continue;
                foreach (var f in Directory.EnumerateFiles(seg, "seg*.lua", SearchOption.AllDirectories))
                {
                    var rel = f.Substring(dir.Length).TrimStart('\\');
                    if (IsPlain(rel)) found.Add(rel);
                }
            }
            return found;
        }

        static byte[] Read(string path)
        {
            for (int i = 0; i < 3; i++)
            {
                try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
                catch (IOException) { Thread.Sleep(50); }    // being written: a moment later
            }
            return null;
        }

        static bool Copy(byte[] bytes, string path)
        {
            if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
    }
}
