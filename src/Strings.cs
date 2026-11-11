using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Qd.HellForge
{
    /// <summary>A strings resource, the game's table of one language's text (reader and writer
    /// from HD2QD Mod Repair's Subtitles.cs, the same author's): the magic 0x3E85F3AE, a format
    /// version, the entry count, the language's hash, then the ids, the absolute offsets of the
    /// texts and one blob of null-terminated UTF-8; one text may be pointed at twice.
    ///
    /// A text mod replaces a whole table, built on the official game's. HellForge's older game has
    /// a table of its own for the same id, with lines the newer one changed, added or dropped
    /// (2026-11-08: HD2 BFV Kill Feedback's 15 tables, about 1.5 % longer on the newer build).
    /// For HellForge the mod's own changes are carried onto the older table (a three-way merge):
    /// each line the mod changed against the official table takes the mod's text, every other
    /// line keeps HellForge's own, and lines only the mod has are added.</summary>
    static class Strings
    {
        public const uint Magic = 0x3E85F3AE;

        public sealed class Table
        {
            public uint Version, Language;
            public uint[] Ids;
            public byte[][] Texts;

            public Dictionary<uint, byte[]> ById()
            {
                var by = new Dictionary<uint, byte[]>(Ids.Length);
                for (int i = 0; i < Ids.Length; i++) by[Ids[i]] = Texts[i];
                return by;
            }
        }

        public static Table Parse(byte[] b)
        {
            if (b == null || b.Length < 12 || BitConverter.ToUInt32(b, 0) != Magic) throw new InvalidDataException("not a strings table");
            uint version = BitConverter.ToUInt32(b, 4), count = BitConverter.ToUInt32(b, 8);
            if (count > 1 << 20 || 12L + (count > 0 ? 4 : 0) + 8L * count > b.Length) throw new InvalidDataException("the strings table's header is damaged");
            var t = new Table { Version = version, Language = count > 0 ? BitConverter.ToUInt32(b, 12) : 0, Ids = new uint[count], Texts = new byte[count][] };
            int n = (int)count, at = 12 + (count > 0 ? 4 : 0);
            for (int i = 0; i < n; i++) t.Ids[i] = BitConverter.ToUInt32(b, at + 4 * i);
            int blob = at + 8 * n;
            for (int i = 0; i < n; i++)
            {
                uint off = BitConverter.ToUInt32(b, at + 4 * n + 4 * i);
                if (off < blob || off >= b.Length) throw new InvalidDataException("a text of the strings table lies outside the texts");
                int end = Array.IndexOf(b, (byte)0, (int)off);
                if (end < 0) throw new InvalidDataException("a text of the strings table does not end");
                t.Texts[i] = new byte[end - (int)off];
                Buffer.BlockCopy(b, (int)off, t.Texts[i], 0, t.Texts[i].Length);
            }
            return t;
        }

        public static byte[] Write(uint version, uint language, uint[] ids, byte[][] texts)
        {
            if (ids.Length != texts.Length) throw new InvalidDataException("a table of " + ids.Length + " entries got " + texts.Length + " texts");
            var seen = new Dictionary<byte[], int>(Bytes.Default);
            var blob = new List<byte>();
            var offs = new int[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                if (!seen.TryGetValue(texts[i], out int at))
                {
                    at = blob.Count; seen[texts[i]] = at;
                    blob.AddRange(texts[i]); blob.Add(0);
                }
                offs[i] = at;
            }
            using (var o = new MemoryStream())
            {
                int head = 16;
                o.Write(BitConverter.GetBytes(Magic), 0, 4);
                o.Write(BitConverter.GetBytes(version), 0, 4);
                o.Write(BitConverter.GetBytes((uint)ids.Length), 0, 4);
                if (ids.Length > 0) o.Write(BitConverter.GetBytes(language), 0, 4); else head = 12;
                for (int i = 0; i < ids.Length; i++) o.Write(BitConverter.GetBytes(ids[i]), 0, 4);
                for (int i = 0; i < ids.Length; i++) o.Write(BitConverter.GetBytes(head + 8 * ids.Length + offs[i]), 0, 4);
                o.Write(blob.ToArray(), 0, blob.Count);
                return o.ToArray();
            }
        }

        /// <summary>The mod's changes carried onto HellForge's table: HellForge's entries in their
        /// order with its version and language, each entry the mod changed against the official
        /// table taking the mod's text, the mod's own entries added at the end. How many entries
        /// took the mod's text comes back in changed. Throws when the three are not one language.</summary>
        public static byte[] ThreeWay(Table hellforge, Table official, Table mod, out int changed)
        {
            if (hellforge.Language != mod.Language || official.Language != mod.Language)
                throw new InvalidDataException("the tables are for different languages");
            var off = official.ById();
            var mine = mod.ById();
            var ids = new List<uint>(hellforge.Ids);
            var have = new HashSet<uint>(hellforge.Ids);
            ids.AddRange(mod.Ids.Where(id => !have.Contains(id) && !off.ContainsKey(id)).Distinct());
            var hf = hellforge.ById();
            var texts = new byte[ids.Count][];
            changed = 0;
            for (int i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                bool modChanged = mine.TryGetValue(id, out var m) && (!off.TryGetValue(id, out var o) || !Bytes.Default.Equals(o, m));
                if (modChanged) { texts[i] = m; changed++; }
                else texts[i] = hf.TryGetValue(id, out var h) ? h : m;
            }
            var result = Write(hellforge.Version, hellforge.Language, ids.ToArray(), texts);
            Parse(result);                                     // it reads back
            return result;
        }

        sealed class Bytes : IEqualityComparer<byte[]>
        {
            public static readonly Bytes Default = new Bytes();
            public bool Equals(byte[] a, byte[] b) => a != null && b != null && a.Length == b.Length && a.SequenceEqual(b);
            public int GetHashCode(byte[] b) { unchecked { int h = 17; foreach (var x in b) h = h * 31 + x; return h; } }
        }
    }
}
