using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Hd2Qd.HellForge
{
    /// <summary>What in a mod made for the official game is known to go wrong on HellForge's
    /// older game version (build 25480438), read from the mod's own patch files only:
    /// - render_config: a whole engine configuration; one built on the newer game turned the whole
    ///   scene false-colour on HellForge (2026-11-08, Super Holo HUD): it must be rebuilt per build;
    /// - strings tables: the game's text, whose lines differ between the builds (a mod's table
    ///   built on the newer one shows wrong or missing text);
    /// - Lua that calls Boot Mod Loader's own API (CowboyBingusModLoader.game): HellForge runs
    ///   Bingus Shared Loader, never Boot Mod Loader.
    /// A full comparison of every overridden resource between the two builds needs both games'
    /// tables and comes with the manager's deploy-target support.</summary>
    static class Scan
    {
        public const ulong RenderConfig = 0x27862FE24795319C;
        public const ulong Strings = 0x0D972BAB10B40FD3;
        public const ulong Lua = 0xA14E8DFA2CD117E2;
        const uint Magic = 0xF0000011;

        public sealed class Facts
        {
            public bool RenderConfig, Strings, BootModLoaderApi, HasLua, Unreadable;
            public bool Any => RenderConfig || Strings || BootModLoaderApi;
        }

        static readonly Regex BmlGame = new Regex(@"CowboyBingusModLoader\s*\.\s*game\b");
        static readonly Dictionary<string, (string Stamp, Facts Facts)> cache = new Dictionary<string, (string, Facts)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The facts of a mod folder; read again only when its patch files change.</summary>
        public static Facts Of(string folder)
        {
            List<string> patches;
            try
            {
                patches = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*.patch_*", SearchOption.AllDirectories).Where(f => Regex.IsMatch(Path.GetFileName(f), @"^[0-9a-fA-F]{16}\.patch_\d+$")).OrderBy(f => f).ToList()
                    : new List<string>();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return new Facts { Unreadable = true }; }
            var stamp = string.Join(";", patches.Select(p => { var fi = new FileInfo(p); return p + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks; }));
            lock (cache)
                if (cache.TryGetValue(folder, out var hit) && hit.Stamp == stamp) return hit.Facts;
            var facts = new Facts();
            foreach (var p in patches)
            {
                try { Read(p, facts); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) { facts.Unreadable = true; }
            }
            lock (cache) cache[folder] = (stamp, facts);
            return facts;
        }

        /// <summary>One patch file's table: a 0x48-byte head, 32-byte type rows, then an 80-byte
        /// row per resource (id, type, main offset at 0x10, main size at 0x38).</summary>
        static void Read(string path, Facts facts)
        {
            using (var f = File.OpenRead(path))
            using (var r = new BinaryReader(f))
            {
                if (f.Length < 0x48) return;
                if (r.ReadUInt32() != Magic) return;
                uint types = r.ReadUInt32(), files = r.ReadUInt32();
                if (types > 4096 || files > 200000) return;
                long rows = 0x48 + types * 32L;
                if (rows + files * 80L > f.Length) return;
                for (uint i = 0; i < files; i++)
                {
                    f.Position = rows + i * 80L;
                    r.ReadUInt64();
                    ulong type = r.ReadUInt64();
                    ulong offset = r.ReadUInt64();
                    if (type == RenderConfig) facts.RenderConfig = true;
                    else if (type == Strings) facts.Strings = true;
                    else if (type == Lua)
                    {
                        facts.HasLua = true;
                        f.Position = rows + i * 80L + 0x38;
                        uint size = r.ReadUInt32();
                        if (size == 0 || offset + size > (ulong)f.Length || size > 32 << 20) continue;
                        f.Position = (long)offset;
                        var text = Encoding.UTF8.GetString(r.ReadBytes((int)size));
                        if (BmlGame.IsMatch(text)) facts.BootModLoaderApi = true;
                    }
                }
            }
        }
    }
}
