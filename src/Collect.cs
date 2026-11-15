using System;
using System.Collections.Generic;
using System.IO;

namespace Hd2Qd.HellForge
{
    /// <summary>A mod HellForge has from elsewhere (put in its mods folder by hand, or by HellForge's
    /// own Mods page) put into Quick Deploy's library from HellForge's own copy (the user,
    /// 2026-11-16: the download is often lost, and HellForge's uninstall deletes the mods folder).
    /// The manager copies it into its mod folder (host "add_to_library"), HellForge's copy is taken
    /// out, and a deploy places the library's copy back (hard links on the same drive).
    /// HellForge's own mods and Local Stats are never offered: HellForge makes them itself.</summary>
    static class Collect
    {
        /// <summary>Whether a mod of HellForge's list can go into the library: one HellForge has from
        /// elsewhere (neither placed by this plugin's window nor by the deploy target), not
        /// HellForge's own, not Local Stats, and with a folder or zip to copy.</summary>
        public static bool Offered(Hf.Mod m, bool placedByPlugin) =>
            !placedByPlugin && !m.Has("bundled") && !m.Has("local-stats") && (m.Folder != null || m.Archive != null);

        /// <summary>Why it cannot go now, or null: the manager is too old for it, or its list in use
        /// is not HellForge's (the mod is added to the list in use, on or off as in HellForge).</summary>
        public static string Blocked(Dictionary<string, object> host)
        {
            if (!host.ContainsKey("add_to_library")) return "collect_needs_newer";
            var target = host.TryGetValue("target", out var t) && t is Func<string> f ? f() : null;
            return target == "hellforge" ? null : "collect_needs_target";
        }

        /// <summary>What is copied: the zip HellForge was given, else its folder.</summary>
        public static string Source(string install, Hf.Mod m)
        {
            if (!string.IsNullOrEmpty(m.Archive))
                return Path.IsPathRooted(m.Archive) ? m.Archive : Path.Combine(Install.Mods(install), m.Archive);
            return m.Folder;
        }

        /// <summary>The mod into the library, then out of HellForge, then the deploy that places the
        /// library's copy. Returns the deploy's answer; HfError when the manager refused the add
        /// (HellForge's copy is then left as it was).</summary>
        public static Dictionary<string, object> Run(Dictionary<string, object> host, string home, string install, Hf.Mod m)
        {
            if (Blocked(host) is string why) throw new HfError(why);
            if (Hf.ReadStatus(install).Running) throw new HfError("a session is running");
            var add = (Func<string, string, bool, string>)host["add_to_library"];
            var from = Source(install, m);
            if (from == null || (!Directory.Exists(from) && !File.Exists(from))) throw new HfError("not found: " + m.Name);
            // the author's coop value into the copy; the choice stays and is made again after the deploy
            Coop.Unmark(home, install, m);
            try { add(from, "hellforge", m.Enabled); }
            catch (Exception e) when (!(e is HfError))
            {
                // the manager's refusal (a name or a folder the library already has, ...), in its words
                Coop.Reapply(home, install, Hf.ReadMods(install));
                throw new HfError(e.Message);
            }
            var r = Hf.Run(install, "mod-remove", "--id", m.Id);
            if (!r.Ok) throw new HfError(r.Why);
            return Plugin.TargetDeploy(host);
        }
    }
}
