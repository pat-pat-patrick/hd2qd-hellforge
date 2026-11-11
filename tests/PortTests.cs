using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    public class PortTests
    {
        /// <summary>A game's data folder as the game packs it since 2025 (Bundles.cs): bundles.nxa, a
        /// DSAR file whose bytes list each package (size, name, pieces), and bundles.00.nxa, a DSAR
        /// file of the packages' bytes. One stored (not LZ4) chunk each.</summary>
        static void FakeGame(string data, Dictionary<string, byte[]> packages)
        {
            Directory.CreateDirectory(data);
            var names = packages.Keys.ToList();
            var blob = new MemoryStream();
            var at = new Dictionary<string, long>();
            foreach (var n in names) { at[n] = blob.Length; blob.Write(packages[n], 0, packages[n].Length); }
            File.WriteAllBytes(Path.Combine(data, "bundles.00.nxa"), Dsar(blob.ToArray()));
            int np = names.Count;
            int recs = 0x18, namesAt = recs + np * 0x18;
            var nameBytes = new MemoryStream();
            var nameOff = new Dictionary<string, int>();
            foreach (var n in names) { nameOff[n] = namesAt + (int)nameBytes.Length; var b = Encoding.ASCII.GetBytes(n); nameBytes.Write(b, 0, b.Length); nameBytes.WriteByte(0); }
            int itemsAt = namesAt + (int)nameBytes.Length;
            itemsAt = (itemsAt + 15) / 16 * 16;
            var index = new byte[itemsAt + np * 0x10];
            BitConverter.GetBytes((uint)np).CopyTo(index, 0x10);
            Buffer.BlockCopy(nameBytes.ToArray(), 0, index, namesAt, (int)nameBytes.Length);
            for (int k = 0; k < np; k++)
            {
                int bl = recs + k * 0x18;
                BitConverter.GetBytes((ulong)packages[names[k]].Length).CopyTo(index, bl);
                BitConverter.GetBytes((uint)nameOff[names[k]]).CopyTo(index, bl + 8);
                BitConverter.GetBytes(1u).CopyTo(index, bl + 12);
                BitConverter.GetBytes((uint)(itemsAt + k * 0x10)).CopyTo(index, bl + 16);
                int r = itemsAt + k * 0x10;
                BitConverter.GetBytes(0UL).CopyTo(index, r);                    // the piece starts the package
                BitConverter.GetBytes((uint)at[names[k]]).CopyTo(index, r + 8);  // where in bundles.00.nxa
                index[r + 0x0f] = 0;                                             // bundle 00
            }
            File.WriteAllBytes(Path.Combine(data, "bundles.nxa"), Dsar(index));
        }

        static byte[] Dsar(byte[] content)
        {
            var f = new byte[0x40 + content.Length];
            Encoding.ASCII.GetBytes("DSAR").CopyTo(f, 0);
            BitConverter.GetBytes(1u).CopyTo(f, 8);
            BitConverter.GetBytes(0UL).CopyTo(f, 0x20);                         // unpacked offset
            BitConverter.GetBytes(0x40UL).CopyTo(f, 0x28);                      // file offset
            BitConverter.GetBytes((uint)content.Length).CopyTo(f, 0x30);
            BitConverter.GetBytes((uint)content.Length).CopyTo(f, 0x34);
            f[0x38] = 0;                                                        // stored
            Buffer.BlockCopy(content, 0, f, 0x40, content.Length);
            return f;
        }

        /// <summary>A patch file whose resources carry their ids (FakeHellForge.Patch gives every
        /// row the same id).</summary>
        static byte[] Patch(params (ulong Id, ulong Type, byte[] Data)[] res)
        {
            var groups = res.Select(r => r.Type).Distinct().ToList();
            var ordered = res.OrderBy(r => groups.IndexOf(r.Type)).ToList();
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(0xF0000011u); w.Write((uint)groups.Count); w.Write((uint)ordered.Count);
            w.Write(new byte[0x48 - 12]);
            foreach (var t in groups) { w.Write(0UL); w.Write(t); w.Write((ulong)ordered.Count(r => r.Type == t)); w.Write(0x10u); w.Write(0x40u); }
            long data = 0x48 + groups.Count * 32L + ordered.Count * 80L;
            var body = new MemoryStream();
            for (int i = 0; i < ordered.Count; i++)
            {
                var row = new byte[80];
                BitConverter.GetBytes(ordered[i].Id).CopyTo(row, 0);
                BitConverter.GetBytes(ordered[i].Type).CopyTo(row, 8);
                BitConverter.GetBytes((ulong)(data + body.Length)).CopyTo(row, 0x10);
                BitConverter.GetBytes((uint)ordered[i].Data.Length).CopyTo(row, 0x38);
                BitConverter.GetBytes((uint)(i + 1)).CopyTo(row, 0x4C);
                w.Write(row);
                body.Write(ordered[i].Data, 0, ordered[i].Data.Length);
                while (body.Length % 16 != 0) body.WriteByte(0);
            }
            body.Position = 0;
            body.CopyTo(ms);
            return ms.ToArray();
        }

        static byte[] Table(params (uint Id, string Text)[] t) =>
            Strings.Write(1, 0x6F05E1A1, t.Select(x => x.Id).ToArray(), t.Select(x => Encoding.UTF8.GetBytes(x.Text)).ToArray());

        static Dictionary<uint, string> Read(byte[] table)
        {
            var t = Strings.Parse(table);
            return t.Ids.Select((id, i) => (id, Encoding.UTF8.GetString(t.Texts[i]))).ToDictionary(x => x.id, x => x.Item2);
        }

        [Fact]
        public void TheModsChangedLinesGoOntoHellForgesTable()
        {
            var hellforge = Strings.Parse(Table((1, "one"), (2, "two"), (3, "three only on HellForge")));
            var official = Strings.Parse(Table((1, "one"), (2, "two, as the newer game says it"), (4, "a line the newer game added")));
            var mod = Strings.Parse(Table((1, "one"), (2, "MOD"), (4, "a line the newer game added"), (9, "the mod's own")));
            var merged = Read(Strings.ThreeWay(hellforge, official, mod, out int changed));
            Assert.Equal(2, changed);
            Assert.Equal(new uint[] { 1, 2, 3, 9 }, merged.Keys.ToArray());
            Assert.Equal("one", merged[1]);
            Assert.Equal("MOD", merged[2]);
            Assert.Equal("three only on HellForge", merged[3]);
            Assert.Equal("the mod's own", merged[9]);
            Assert.False(merged.ContainsKey(4));            // the newer game's line, which the mod did not change, stays out
            var other = Strings.Parse(Strings.Write(1, 0x12345678, new uint[] { 1 }, new[] { Encoding.UTF8.GetBytes("x") }));
            Assert.Throws<InvalidDataException>(() => Strings.ThreeWay(hellforge, official, other, out _));
        }

        [Fact]
        public void ARewrittenPatchKeepsEveryOtherResourceByteForByte()
        {
            var a = Enumerable.Range(0, 37).Select(i => (byte)i).ToArray();
            var b = Encoding.UTF8.GetBytes("render config");
            var c = Enumerable.Range(0, 5).Select(i => (byte)(200 + i)).ToArray();
            var main = Patch((1, 0xAAAA00000001, a), (2, Scan.RenderConfig, b), (3, 0xAAAA00000001, c));
            var fresh = new byte[] { 9, 9, 9 };
            var outp = Port.Rewrite(main, new HashSet<(ulong, ulong)> { (2, Scan.RenderConfig) }, new Dictionary<(ulong, ulong), byte[]> { [(3, 0xAAAA00000001)] = fresh });
            var (types, rows) = Port.Rows(outp);
            Assert.Equal(1, types);
            Assert.Equal(2, rows.Count);
            Assert.Equal(a, outp.Skip((int)rows[0].Off).Take((int)rows[0].Size).ToArray());
            Assert.Equal(fresh, outp.Skip((int)rows[1].Off).Take((int)rows[1].Size).ToArray());
            Assert.Equal(1u, BitConverter.ToUInt32(outp, rows[0].At + 0x4C));
            Assert.Equal(2u, BitConverter.ToUInt32(outp, rows[1].At + 0x4C));
            Assert.Equal(2UL, BitConverter.ToUInt64(outp, 0x48 + 16));             // the type row's count
            Assert.Equal(0UL, (rows[1].Off - rows[0].Off) % 16);
            Assert.Null(Port.Rewrite(main, new HashSet<(ulong, ulong)> { (1, 0xAAAA00000001), (2, Scan.RenderConfig), (3, 0xAAAA00000001) }, new Dictionary<(ulong, ulong), byte[]>()));
        }

        [Fact]
        public void ALibraryModIsMadeReadyForHellForgesGame()
        {
            using (var f = new FakeHellForge())
            {
                const ulong Tex = Port.Texture, Mat = Port.Material;
                var official = Path.Combine(f.Root, "official", "data");
                var hfData = Path.Combine(f.Install, "game", "data");
                var tex = Encoding.UTF8.GetBytes("the same texture in both games");
                FakeGame(official, new Dictionary<string, byte[]>
                {
                    ["1111111111111111"] = Patch((0xA, Scan.Strings, Table((1, "one"), (2, "two, newer"))), (0xB, Scan.RenderConfig, Encoding.UTF8.GetBytes("newer render config")),
                                                 (0xC, Tex, tex), (0xD, Mat, Encoding.UTF8.GetBytes("newer material")), (0xF, Scan.Lua, Encoding.UTF8.GetBytes("-- newer boot"))),
                });
                FakeGame(hfData, new Dictionary<string, byte[]>
                {
                    ["2222222222222222"] = Patch((0xA, Scan.Strings, Table((1, "one"), (2, "two"), (3, "three"))), (0xB, Scan.RenderConfig, Encoding.UTF8.GetBytes("older render config!")),
                                                 (0xC, Tex, tex), (0xD, Mat, Encoding.UTF8.GetBytes("older material")), (0xF, Scan.Lua, Encoding.UTF8.GetBytes("-- older boot!!"))),
                });
                var lib = Path.Combine(f.Library, "Big Look_0123456789ab");
                Directory.CreateDirectory(lib);
                File.WriteAllText(Path.Combine(lib, "manifest.json"), "{\"Guid\": \"55555555-5555-5555-5555-555555555555\", \"Name\": \"Big Look\"}");
                var modMain = Patch((0xA, Scan.Strings, Table((1, "one"), (2, "MOD TWO"))), (0xB, Scan.RenderConfig, Encoding.UTF8.GetBytes("the mod's config")),
                                    (0xC, Tex, Encoding.UTF8.GetBytes("the mod's texture")), (0xD, Mat, Encoding.UTF8.GetBytes("the mod's material")),
                                    (0xE, Tex, Encoding.UTF8.GetBytes("the mod's own")), (0xF, Scan.Lua, Encoding.UTF8.GetBytes("-- the mod's boot")));
                var libPatch = Path.Combine(lib, "0123456789abcdef.patch_0");
                File.WriteAllBytes(libPatch, modMain);
                File.WriteAllBytes(libPatch + ".gpu_resources", new byte[] { 1, 2, 3 });

                var r = Port.Check(f.Home, official, hfData, lib);
                Assert.Equal(Port.Level.Partial, r.Level);
                Assert.Equal(6, r.Resources);
                Assert.Equal(1, r.Same);
                Assert.Equal(1, r.Own);
                Assert.Equal(1, r.Merged);
                Assert.Equal(2, r.LeftOut);
                Assert.Equal(new Dictionary<ulong, int> { [Mat] = 1 }, r.KeptDiffering);
                Assert.True(File.Exists(Path.Combine(f.Home, "hellforge", "compare.json")));

                var e = Placed.Put(f.Home, f.Install, "Big Look", lib, Hf.ReadMods(f.Install), official);
                Assert.Equal("Partial", e.Level);
                Assert.Equal(1, e.Merged);
                var placed = Path.Combine(f.Mods, e.Folder, "0123456789abcdef.patch_0");
                var outp = File.ReadAllBytes(placed);
                var rows = Port.Rows(outp).Rows;
                Assert.Equal(new ulong[] { 0xA, 0xC, 0xD, 0xE }, rows.Select(x => x.Id).OrderBy(x => x).ToArray());
                var table = rows.Single(x => x.Id == 0xA);
                var merged = Read(outp.Skip((int)table.Off).Take((int)table.Size).ToArray());
                Assert.Equal("MOD TWO", merged[2]);
                Assert.Equal("three", merged[3]);
                Assert.Equal(Encoding.UTF8.GetBytes("the mod's material"), outp.Skip((int)rows.Single(x => x.Id == 0xD).Off).Take((int)rows.Single(x => x.Id == 0xD).Size).ToArray());
                // the library is not written: its patch is as it was and no longer shares its bytes
                Assert.Equal(modMain, File.ReadAllBytes(libPatch));
                Assert.Equal(1, FakeHellForge.Links(libPatch));
                Assert.Equal(2, FakeHellForge.Links(placed + ".gpu_resources"));      // untouched files stay links
                var lines = Plugin.PortLines("zh-CN", e);
                Assert.Equal(3, lines.Count);
                Assert.Contains(lines, l => l.Text.Contains("渲染配置") && l.Text.Contains("LUA"));
                Assert.Contains(lines, l => l.Text.Contains("材质"));

                // a game update makes it ready again
                Assert.False(Placed.GamesChanged(Placed.Load(f.Home, f.Install).Values.Single(), f.Install, official));
                File.SetLastWriteTimeUtc(Path.Combine(hfData, "bundles.nxa"), DateTime.UtcNow.AddMinutes(1));
                Assert.True(Placed.GamesChanged(Placed.Load(f.Home, f.Install).Values.Single(), f.Install, official));
                Assert.Contains(Plugin.Sync(f.Home, f.Install, official), s => s.Contains("Big Look"));
            }
        }

        [Fact]
        public void WithoutTheOfficialGameNothingIsChanged()
        {
            using (var f = new FakeHellForge())
            {
                var lib = f.LibraryPack("Plain", "66666666-6666-6666-6666-666666666666", FakeHellForge.Patch((Scan.RenderConfig, new byte[8])));
                var e = Placed.Put(f.Home, f.Install, "Plain", lib, Hf.ReadMods(f.Install), null);
                Assert.Equal("Unknown", e.Level);
                var placedPatch = Directory.GetFiles(Path.Combine(f.Mods, e.Folder), "*.patch_0", SearchOption.AllDirectories)[0];
                Assert.Equal(2, FakeHellForge.Links(placedPatch));
                Assert.Single(Plugin.PortLines("en", e));
            }
        }
    }
}
