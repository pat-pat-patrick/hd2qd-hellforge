using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Qd.HellForge
{
    /// <summary>The two games a library mod can go to, compared resource by resource: the official
    /// game (the manager's game folder, data\) and HellForge's copy (its game\data, build 25480438,
    /// read only). A mod made for the official game replaces game resources whole; each one it
    /// replaces is found in both games' packages (bundles.nxa: Bundles.cs) and judged:
    /// - Own: in neither game (the mod's own resource), the same on both;
    /// - Same: the game's original is byte for byte the same in both builds;
    /// - Slight: the same sizes, a few bytes differ (2026-11-08: two laser-sight units, float rounding);
    /// - Differs: the originals differ: the mod was most likely made from the official one;
    /// - NewOnly: only the official game has it (HellForge's game never asks for it);
    /// - OldOnly: only HellForge's game has it.
    /// The packages' rows: id, type, main/stream/gpu offsets at 0x10/0x18/0x20, sizes at
    /// 0x38/0x3C/0x40 (hd2_diag port_sweep.py's toc). Verdicts are kept in home\hellforge\
    /// compare.json until either game's bundles.nxa changes.</summary>
    static class GameIndex
    {
        public enum Verdict { Own, Same, Slight, Differs, NewOnly, OldOnly }

        public sealed class Loc
        {
            public string Pkg;
            public ulong MainOff, StreamOff, GpuOff;
            public uint MainSize, StreamSize, GpuSize;
        }

        public static long Stamp(string data)
        {
            var index = new FileInfo(Path.Combine(data, "bundles.nxa"));
            if (!index.Exists) throw new FileNotFoundException("bundles.nxa is not in " + data);
            return index.Length ^ index.LastWriteTimeUtc.Ticks;
        }

        /// <summary>Where each of the keys sits in a game: the first package (by name) holding it.</summary>
        public static Dictionary<(ulong Id, ulong Type), Loc> Locate(Game game, HashSet<(ulong, ulong)> keys, Action<double> progress = null)
        {
            var found = new Dictionary<(ulong, ulong), Loc>();
            var names = game.Packages.Where(n => n.IndexOf('.') < 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
            for (int n = 0; n < names.Count && found.Count < keys.Count; n++)
            {
                if (progress != null && n % 200 == 0) progress((double)n / names.Count);
                var pkg = names[n];
                var pre = game.Read(pkg, 0, 12);
                if (pre.Length < 12) continue;
                uint magic = Game.U32(pre, 0), tc = Game.U32(pre, 4), fc = Game.U32(pre, 8);
                if (magic != 0xF0000011 || tc > 4096 || fc > 200000) continue;
                int b = 0x48 + (int)tc * 32;
                var head = game.Read(pkg, 0, b + (int)fc * 80);
                for (int i = 0; i < fc; i++)
                {
                    int o = b + i * 80;
                    if (o + 80 > head.Length) break;
                    var k = (Game.U64(head, o), Game.U64(head, o + 8));
                    if (!keys.Contains(k) || found.ContainsKey(k)) continue;
                    found[k] = new Loc
                    {
                        Pkg = pkg, MainOff = Game.U64(head, o + 0x10), StreamOff = Game.U64(head, o + 0x18), GpuOff = Game.U64(head, o + 0x20),
                        MainSize = Game.U32(head, o + 0x38), StreamSize = Game.U32(head, o + 0x3C), GpuSize = Game.U32(head, o + 0x40),
                    };
                }
            }
            progress?.Invoke(1);
            return found;
        }

        /// <summary>A resource's main bytes in a game.</summary>
        public static byte[] Main(Game game, Loc l) => game.Read(l.Pkg, l.MainOff, (int)l.MainSize);

        static string Hash(Game game, Loc l)
        {
            using (var sha = SHA256.Create())
            {
                void Part(string pkg, ulong off, uint size)
                {
                    var head = BitConverter.GetBytes(size);
                    sha.TransformBlock(head, 0, 4, null, 0);
                    // read in pieces: a texture's gpu part may be large
                    for (ulong at = 0; at < size; at += 1 << 22)
                    {
                        int len = (int)Math.Min(1UL << 22, size - at);
                        var bytes = game.Read(pkg, off + at, len);
                        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                    }
                }
                Part(l.Pkg, l.MainOff, l.MainSize);
                if (l.StreamSize > 0) Part(l.Pkg + ".stream", l.StreamOff, l.StreamSize);
                if (l.GpuSize > 0) Part(l.Pkg + ".gpu_resources", l.GpuOff, l.GpuSize);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        static readonly object gate = new object();

        static string CacheFile(string home) => Path.Combine(home, "hellforge", "compare.json");

        static string K((ulong Id, ulong Type) k) => k.Id.ToString("x16") + ":" + k.Type.ToString("x16");

        /// <summary>Each key's verdict; what is not known yet is read from both games (seconds the
        /// first time: every package's header, then only the resources asked for).</summary>
        public static Dictionary<(ulong Id, ulong Type), Verdict> Compare(string home, string officialData, string hellforgeData,
                                                                           IEnumerable<(ulong, ulong)> keys, Action<double> progress = null)
        {
            lock (gate)
            {
                long sn = Stamp(officialData), so = Stamp(hellforgeData);
                var cache = ReadCache(home, sn, so);
                var want = new HashSet<(ulong, ulong)>(keys);
                var result = new Dictionary<(ulong, ulong), Verdict>();
                var missing = new HashSet<(ulong, ulong)>();
                foreach (var k in want)
                    if (cache.TryGetValue(K(k), out var v) && Enum.TryParse<Verdict>(v, out var known)) result[k] = known;
                    else missing.Add(k);
                if (missing.Count == 0) return result;
                using (var gn = new Game(officialData))
                using (var go = new Game(hellforgeData))
                {
                    var ln = Locate(gn, missing, p => progress?.Invoke(0.4 * p));
                    var lo = Locate(go, missing, p => progress?.Invoke(0.4 + 0.4 * p));
                    int i = 0;
                    foreach (var k in missing)
                    {
                        progress?.Invoke(0.8 + 0.2 * i++ / missing.Count);
                        ln.TryGetValue(k, out var a);
                        lo.TryGetValue(k, out var b);
                        Verdict v;
                        if (a == null && b == null) v = Verdict.Own;
                        else if (b == null) v = Verdict.NewOnly;
                        else if (a == null) v = Verdict.OldOnly;
                        else if (Hash(gn, a) == Hash(go, b)) v = Verdict.Same;
                        else v = a.MainSize == b.MainSize && a.StreamSize == b.StreamSize && a.GpuSize == b.GpuSize ? Verdict.Slight : Verdict.Differs;
                        result[k] = v;
                        cache[K(k)] = v.ToString();
                    }
                }
                progress?.Invoke(1);
                WriteCache(home, sn, so, cache);
                return result;
            }
        }

        /// <summary>The main bytes of one resource in both games (official, HellForge's); null where
        /// a game does not have it.</summary>
        public static (byte[] Official, byte[] HellForge) Originals(string officialData, string hellforgeData, (ulong, ulong) key)
        {
            var keys = new HashSet<(ulong, ulong)> { key };
            byte[] Of(string data)
            {
                using (var g = new Game(data))
                    return Locate(g, keys).TryGetValue(key, out var l) ? Main(g, l) : null;
            }
            return (Of(officialData), Of(hellforgeData));
        }

        static Dictionary<string, string> ReadCache(string home, long sn, long so)
        {
            try
            {
                var p = CacheFile(home);
                if (!File.Exists(p)) return new Dictionary<string, string>();
                var d = Json.Obj(File.ReadAllText(p, Encoding.UTF8));
                if (Json.Long(d, "official") != sn || Json.Long(d, "hellforge") != so) return new Dictionary<string, string>();
                return (Json.Obj(d, "verdicts") ?? new Dictionary<string, object>()).ToDictionary(kv => kv.Key, kv => kv.Value as string);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return new Dictionary<string, string>(); }
        }

        static void WriteCache(string home, long sn, long so, Dictionary<string, string> cache)
        {
            try
            {
                Placed.WriteText(CacheFile(home), Json.Write(new Dictionary<string, object>
                {
                    ["official"] = sn, ["hellforge"] = so, ["verdicts"] = cache.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                }));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }
}
