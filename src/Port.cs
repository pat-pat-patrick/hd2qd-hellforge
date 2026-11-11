using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hd2Qd.HellForge
{
    /// <summary>A library mod made for the official game, made ready for HellForge's older game
    /// (build 25480438). Static resources (models, textures, materials, sounds, text) replace game
    /// resources whole, so most go over as they are; the rest is decided per resource from the two
    /// games' originals (GameIndex):
    /// - the original is the same in both games, or the resource is the mod's own: kept as it is;
    /// - a strings table whose originals differ: the mod's changed lines carried onto HellForge's
    ///   table (Strings.ThreeWay), so lines the newer game changed do not leak in;
    /// - a render_config (a whole engine configuration): left out. One built on the newer game
    ///   turned the whole scene false-colour on HellForge (2026-11-08, Super Holo HUD); without
    ///   it the game uses its own, and the rest of the mod still loads;
    /// - a shader_library (compiled shaders; HellForge rebuilds warbond materials in the old
    ///   shader layout, so the layouts differ) and a replaced game Lua script (a loader's boot,
    ///   written against the newer game's scripts): left out the same way;
    /// - anything else whose originals differ: kept, and named, since the mod was most likely made
    ///   from the newer original (a model, a texture, a material, a sound bank).
    /// The library's files are never written: a converted patch file is a new file in HellForge's
    /// copy of the mod, every other file stays a hard link.</summary>
    static class Port
    {
        public enum Level { Ready, Adjusted, Partial, Unknown }

        public sealed class Step
        {
            public string File;                     // the patch file, relative to the mod's folder (its name in HellForge's copy)
            public string Src;                      // where its bytes are read from
            public (ulong Id, ulong Type) Key;
            public string What;                     // "merge", "leave_out"
        }

        public sealed class Report
        {
            public Level Level = Level.Ready;
            public int Resources, Same, Own, Slight, Differs, NewOnly;
            /// <summary>Resources kept although their originals differ, by type.</summary>
            public Dictionary<ulong, int> KeptDiffering = new Dictionary<ulong, int>();
            /// <summary>Resources left out, by type.</summary>
            public Dictionary<ulong, int> LeftOutTypes = new Dictionary<ulong, int>();
            public List<Step> Steps = new List<Step>();
            public int Merged => Steps.Count(s => s.What == "merge");
            public int LeftOut => Steps.Count(s => s.What == "leave_out");
            public string Why;                      // Unknown: why it could not be checked
        }

        public const ulong ShaderLibrary = 0xE5EE32A477239A93, Material = 0xEAC0B497876ADEDF, WwiseBank = 0x535A7BD3E650D799,
                           Unit = 0xE0A48D0BE9A7453F, Texture = 0xCD4238C6A0C69E32;

        /// <summary>The types left out when the two games' originals differ.</summary>
        public static readonly HashSet<ulong> LeaveOut = new HashSet<ulong> { Scan.RenderConfig, ShaderLibrary, Scan.Lua };

        /// <summary>A type's word key (Lang: type_*).</summary>
        public static string TypeKey(ulong type)
        {
            switch (type)
            {
                case Scan.RenderConfig: return "type_render_config";
                case ShaderLibrary: return "type_shader_library";
                case Scan.Lua: return "type_lua";
                case Scan.Strings: return "type_strings";
                case Material: return "type_material";
                case WwiseBank: return "type_wwise_bank";
                case Unit: return "type_unit";
                case Texture: return "type_texture";
                default: return "type_other";
            }
        }

        static readonly Regex Main = new Regex(@"^[0-9a-fA-F]{16}\.patch_\d+$");

        public static List<string> PatchFiles(string folder) =>
            Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*.patch_*", SearchOption.AllDirectories).Where(f => Main.IsMatch(Path.GetFileName(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();

        public sealed class Row
        {
            public int At;
            public ulong Id, Type, Off;
            public uint Size;
        }

        /// <summary>A patch file's header: the type and resource counts, and each resource's row.</summary>
        public static (int Types, List<Row> Rows) Rows(byte[] main)
        {
            if (main.Length < 0x48 || BitConverter.ToUInt32(main, 0) != 0xF0000011) throw new InvalidDataException("not a patch archive");
            uint tc = BitConverter.ToUInt32(main, 4), fc = BitConverter.ToUInt32(main, 8);
            if (tc > 4096 || fc > 1000000 || 0x48L + tc * 32 + fc * 80L > main.Length) throw new InvalidDataException("the archive's header is damaged");
            var rows = new List<Row>((int)fc);
            int b = 0x48 + (int)tc * 32;
            for (int i = 0; i < fc; i++)
            {
                int o = b + i * 80;
                var r = new Row { At = o, Id = BitConverter.ToUInt64(main, o), Type = BitConverter.ToUInt64(main, o + 8), Off = BitConverter.ToUInt64(main, o + 0x10), Size = BitConverter.ToUInt32(main, o + 0x38) };
                if (r.Off + r.Size > (ulong)main.Length) throw new InvalidDataException("a resource lies outside the archive");
                rows.Add(r);
            }
            return ((int)tc, rows);
        }

        static List<(ulong Id, ulong Type)> KeysOf(string file)
        {
            // only the header is read: the rows end before the first resource's bytes
            using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
            {
                var head = new byte[12];
                if (f.Read(head, 0, 12) < 12 || BitConverter.ToUInt32(head, 0) != 0xF0000011) return new List<(ulong, ulong)>();
                uint tc = BitConverter.ToUInt32(head, 4), fc = BitConverter.ToUInt32(head, 8);
                if (tc > 4096 || fc > 1000000) return new List<(ulong, ulong)>();
                long need = 0x48 + tc * 32L + fc * 80L;
                if (need > f.Length) return new List<(ulong, ulong)>();
                var all = new byte[need];
                f.Position = 0;
                int got = 0;
                while (got < need) { int r = f.Read(all, got, (int)need - got); if (r <= 0) break; got += r; }
                int b = 0x48 + (int)tc * 32;
                return Enumerable.Range(0, (int)fc).Select(i => (BitConverter.ToUInt64(all, b + i * 80), BitConverter.ToUInt64(all, b + i * 80 + 8))).ToList();
            }
        }

        /// <summary>What a mod needs for HellForge. officialData null (no official game folder set):
        /// Unknown, nothing changed.</summary>
        public static Report Check(string home, string officialData, string hellforgeData, string modFolder, Action<double> progress = null) =>
            CheckMany(home, officialData, hellforgeData, new[] { modFolder }, progress)[modFolder];

        /// <summary>What each of several mods needs; the two games are read once for all of them.</summary>
        public static Dictionary<string, Report> CheckMany(string home, string officialData, string hellforgeData, IEnumerable<string> modFolders, Action<double> progress = null) =>
            CheckSets(home, officialData, hellforgeData,
                      modFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(f => f, f => PatchFiles(f).Select(p => (p, p.Substring(f.TrimEnd('\\').Length + 1))).ToList(), StringComparer.OrdinalIgnoreCase),
                      progress);

        /// <summary>The same for sets of files by a key of the caller's: each file's source and its
        /// name in HellForge's copy (a deploy target's plan: the options already chosen).</summary>
        public static Dictionary<string, Report> CheckSets(string home, string officialData, string hellforgeData,
                                                            Dictionary<string, List<(string Src, string Rel)>> sets, Action<double> progress = null)
        {
            var reports = new Dictionary<string, Report>(StringComparer.OrdinalIgnoreCase);
            var lists = new Dictionary<string, List<(string Rel, string Src, List<(ulong Id, ulong Type)> Keys)>>(StringComparer.OrdinalIgnoreCase);
            bool official = officialData != null && File.Exists(Path.Combine(officialData, "bundles.nxa"));
            foreach (var set in sets)
            {
                var r = reports[set.Key] = new Report();
                if (!official) { r.Level = Level.Unknown; r.Why = "no_official_game"; continue; }
                var perFile = new List<(string, string, List<(ulong, ulong)>)>();
                try
                {
                    foreach (var (src, rel) in set.Value.Where(x => Main.IsMatch(Path.GetFileName(x.Rel)))) perFile.Add((rel, src, KeysOf(src)));
                    lists[set.Key] = perFile;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { r.Level = Level.Unknown; r.Why = e.Message; }
            }
            var all = lists.Values.SelectMany(l => l.SelectMany(p => p.Item3)).Distinct().ToList();
            if (all.Count == 0) return reports;
            var verdicts = GameIndex.Compare(home, officialData, hellforgeData, all, progress);
            foreach (var kv in lists)
            {
                var r = reports[kv.Key];
                foreach (var (rel, src, keys) in kv.Value)
                    foreach (var k in keys)
                    {
                        r.Resources++;
                        var v = verdicts[k];
                        switch (v)
                        {
                            case GameIndex.Verdict.Same: r.Same++; break;
                            case GameIndex.Verdict.Own: case GameIndex.Verdict.OldOnly: r.Own++; break;
                            case GameIndex.Verdict.NewOnly: r.NewOnly++; break;
                            case GameIndex.Verdict.Slight: r.Slight++; break;
                            case GameIndex.Verdict.Differs: r.Differs++; break;
                        }
                        if (LeaveOut.Contains(k.Type) && (v == GameIndex.Verdict.Differs || v == GameIndex.Verdict.Slight))
                        {
                            r.Steps.Add(new Step { File = rel, Src = src, Key = k, What = "leave_out" });
                            r.LeftOutTypes[k.Type] = r.LeftOutTypes.TryGetValue(k.Type, out var lo) ? lo + 1 : 1;
                        }
                        else if (k.Type == Scan.Strings && (v == GameIndex.Verdict.Differs || v == GameIndex.Verdict.Slight))
                            r.Steps.Add(new Step { File = rel, Src = src, Key = k, What = "merge" });
                        else if (v == GameIndex.Verdict.Differs || v == GameIndex.Verdict.Slight)
                            r.KeptDiffering[k.Type] = r.KeptDiffering.TryGetValue(k.Type, out var n) ? n + 1 : 1;
                    }
                r.Level = r.LeftOut > 0 ? Level.Partial : r.Merged > 0 || r.KeptDiffering.Count > 0 ? Level.Adjusted : Level.Ready;
            }
            return reports;
        }

        /// <summary>HellForge's copy of the mod (dest, already the library's files hard-linked or
        /// copied) brought to what the report says: each patch file with a step written anew (the
        /// link replaced by a file of its own), or taken out when nothing of it is left. A merge
        /// that fails keeps the mod's table and is named in the returned notes.</summary>
        public static List<string> Apply(Report r, string officialData, string hellforgeData, string modFolder, string dest)
        {
            var notes = new List<string>();
            if (r.Steps.Count == 0) return notes;
            var merges = new HashSet<(ulong, ulong)>(r.Steps.Where(s => s.What == "merge").Select(s => s.Key));
            Dictionary<(ulong, ulong), byte[]> official = new Dictionary<(ulong, ulong), byte[]>(), hellforge = new Dictionary<(ulong, ulong), byte[]>();
            if (merges.Count > 0)
            {
                using (var g = new Game(officialData)) foreach (var kv in GameIndex.Locate(g, merges)) official[kv.Key] = GameIndex.Main(g, kv.Value);
                using (var g = new Game(hellforgeData)) foreach (var kv in GameIndex.Locate(g, merges)) hellforge[kv.Key] = GameIndex.Main(g, kv.Value);
            }
            foreach (var group in r.Steps.GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase))
            {
                var source = group.First().Src ?? Path.Combine(modFolder, group.Key);
                var target = Path.Combine(dest, group.Key);
                var main = File.ReadAllBytes(source);
                var leave = new HashSet<(ulong, ulong)>(group.Where(s => s.What == "leave_out").Select(s => s.Key));
                var fresh = new Dictionary<(ulong, ulong), byte[]>();
                var rows = Rows(main).Rows;
                foreach (var s in group.Where(x => x.What == "merge"))
                {
                    var row = rows.First(x => x.Id == s.Key.Id && x.Type == s.Key.Type);
                    var mine = new byte[row.Size];
                    Buffer.BlockCopy(main, (int)row.Off, mine, 0, mine.Length);
                    try
                    {
                        if (!official.TryGetValue(s.Key, out var o) || !hellforge.TryGetValue(s.Key, out var h)) throw new InvalidDataException("a game's own table cannot be read");
                        fresh[s.Key] = Strings.ThreeWay(Strings.Parse(h), Strings.Parse(o), Strings.Parse(mine), out _);
                    }
                    catch (InvalidDataException e) { notes.Add(s.Key.Id.ToString("x16") + ": " + e.Message); }
                }
                var rewritten = Rewrite(main, leave, fresh);
                if (File.Exists(target)) File.Delete(target);            // a hard link: never written through
                if (rewritten == null)
                {
                    foreach (var ext in new[] { ".gpu_resources", ".stream" })
                        if (File.Exists(target + ext)) File.Delete(target + ext);
                }
                else File.WriteAllBytes(target, rewritten);
            }
            return notes;
        }

        /// <summary>A patch file's main bytes without some resources and with others' bytes
        /// replaced; null when nothing is left. Laid out as the SDK writes one (hd2_diag
        /// lock_archive.write_archive): type rows (0, type, count, 0x10, 0x40) in the original
        /// order, the rows grouped by type and numbered from 1, main bytes aligned to 16. The rows'
        /// gpu and stream offsets are kept: the .gpu_resources and .stream files are not changed
        /// (a left-out resource's bytes there are only no longer pointed at).</summary>
        public static byte[] Rewrite(byte[] main, HashSet<(ulong, ulong)> leaveOut, Dictionary<(ulong, ulong), byte[]> fresh)
        {
            var (tc, rows) = Rows(main);
            var kept = rows.Where(r => !leaveOut.Contains((r.Id, r.Type))).ToList();
            if (kept.Count == 0) return null;
            var typeOrder = new List<ulong>();
            for (int t = 0; t < tc; t++) typeOrder.Add(BitConverter.ToUInt64(main, 0x48 + t * 32 + 8));
            foreach (var r in kept) if (!typeOrder.Contains(r.Type)) typeOrder.Add(r.Type);
            var types = typeOrder.Where(t => kept.Any(r => r.Type == t)).ToList();
            kept = kept.OrderBy(r => types.IndexOf(r.Type)).ToList();                 // stable: grouped by type
            int dataAt = 0x48 + types.Count * 32 + kept.Count * 80;
            var outp = new MemoryStream();
            outp.Write(main, 0, 0x48);
            foreach (var t in types)
            {
                int orig = Enumerable.Range(0, tc).FirstOrDefault(i => BitConverter.ToUInt64(main, 0x48 + i * 32 + 8) == t);
                var row = new byte[32];
                Buffer.BlockCopy(main, 0x48 + orig * 32, row, 0, 32);
                Array.Copy(BitConverter.GetBytes((ulong)kept.Count(r => r.Type == t)), 0, row, 16, 8);
                outp.Write(row, 0, 32);
            }
            var data = new MemoryStream();
            var rowBytes = new List<byte[]>();
            for (int i = 0; i < kept.Count; i++)
            {
                var r = kept[i];
                var bytes = fresh.TryGetValue((r.Id, r.Type), out var f) ? f : Slice(main, (int)r.Off, (int)r.Size);
                var row = new byte[80];
                Buffer.BlockCopy(main, r.At, row, 0, 80);
                Array.Copy(BitConverter.GetBytes((ulong)(dataAt + data.Length)), 0, row, 0x10, 8);
                Array.Copy(BitConverter.GetBytes((uint)bytes.Length), 0, row, 0x38, 4);
                Array.Copy(BitConverter.GetBytes((uint)(i + 1)), 0, row, 0x4C, 4);
                rowBytes.Add(row);
                data.Write(bytes, 0, bytes.Length);
                while (data.Length % 16 != 0) data.WriteByte(0);
            }
            foreach (var row in rowBytes) outp.Write(row, 0, 80);
            data.Position = 0;
            data.CopyTo(outp);
            var o = outp.ToArray();
            Array.Copy(BitConverter.GetBytes((uint)types.Count), 0, o, 4, 4);
            Array.Copy(BitConverter.GetBytes((uint)kept.Count), 0, o, 8, 4);
            return o;
        }

        static byte[] Slice(byte[] b, int at, int len)
        {
            var t = new byte[len];
            Buffer.BlockCopy(b, at, t, 0, len);
            return t;
        }
    }
}
