using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    public class HellForgeTests
    {
        const string GuidA = "5b8e2c41-9d37-4f6a-b0e3-7c14a9d2f685";

        static Dictionary<string, object> Host(FakeHellForge f, string lang = "en") => new Dictionary<string, object>
        {
            ["api"] = 2L, ["home"] = f.Home, ["lang"] = lang,
            ["library"] = (Func<List<Dictionary<string, object>>>)(() => Directory.GetDirectories(f.Library)
                .Select(d => new Dictionary<string, object> { ["name"] = Path.GetFileName(d).Split('_')[0], ["path"] = d, ["enabled"] = true }).ToList()),
        };

        // ---------------------------------------------------------------- finding the installation

        [Fact]
        public void FindsAnInstallationByItsFile()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { Path.Combine(f.Root, "nothing"), f.Install };
                try
                {
                    Assert.Equal(Path.GetFullPath(f.Install), Install.Find(null));
                    Assert.Equal(Path.GetFullPath(f.Install), Install.Find(Path.Combine(f.Root, "not-an-install")));
                    var exe = Path.Combine(f.Install, "app", "cli", "HellForge.Cli.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(exe));
                    Assert.Equal(f.Install, Install.InstallAbove(exe));
                    Install.PlacesOverride = () => new[] { f.Library };
                    Assert.Null(Install.Find(null));
                    File.WriteAllText(Path.Combine(f.Library, "bts-install.json"), "{}");     // the name before the rename
                    Assert.Equal(Path.GetFullPath(f.Library), Install.Find(null));
                }
                finally { Install.PlacesOverride = null; }
            }
        }

        [Fact]
        public void ArgumentsAreQuotedAsWindowsSplitsThem()
        {
            Assert.Equal("mods", Hf.Quote(new[] { "mods" }));
            Assert.Equal("logs --export \"C:\\a b\\c.zip\"", Hf.Quote(new[] { "logs", "--export", @"C:\a b\c.zip" }));
            Assert.Equal("--option \"say \\\"hi\\\"\"", Hf.Quote(new[] { "--option", "say \"hi\"" }));
            Assert.Equal("\"C:\\a b\\\\\"", Hf.Quote(new[] { @"C:\a b\" }));
            Assert.Equal("\"\"", Hf.Quote(new[] { "" }));
        }

        // ---------------------------------------------------------------- HellForge's ids

        [Fact]
        public void IdsAreHellForgesOwn()
        {
            using (var f = new FakeHellForge())
            {
                var withGuid = f.LibraryPack("Super Holo HUD v1.226.1", GuidA.ToUpperInvariant());
                Assert.Equal("pack." + GuidA, Placed.IdOf(withGuid, "x"));
                // no manifest, patches at the top: pack.<slug of the folder name>
                var bare = Path.Combine(f.Library, "Mod Lag Watchdog v0.51.1");
                Directory.CreateDirectory(bare);
                File.WriteAllBytes(Path.Combine(bare, "0123456789abcdef.patch_0"), FakeHellForge.Patch());
                Assert.Equal("pack.mod-lag-watchdog-v0-51-1", Placed.IdOf(bare, "Mod Lag Watchdog v0.51.1"));
                // nothing HellForge takes as a mod
                var readme = Path.Combine(f.Library, "Readme only");
                Directory.CreateDirectory(readme);
                File.WriteAllText(Path.Combine(readme, "README.txt"), "x");
                Assert.Null(Placed.IdOf(readme, "Readme only"));
                Assert.Equal("pack", Placed.Slug("!!!"));
            }
        }

        // ---------------------------------------------------------------- putting library mods into HellForge

        [Fact]
        public void PutLinksTheLibrarysFilesAndRecordsThem()
        {
            using (var f = new FakeHellForge())
            {
                var lib = f.LibraryPack("Super Holo HUD", GuidA);
                var e = Placed.Put(f.Home, f.Install, "Super Holo HUD", lib, Hf.ReadMods(f.Install));
                Assert.True(e.Linked);
                var placedPatch = Path.Combine(f.Mods, e.Folder, "Main", "0123456789abcdef.patch_0");
                Assert.True(File.Exists(placedPatch));
                Assert.Equal(2, FakeHellForge.Links(placedPatch));                    // one file, two names
                Assert.Equal("pack." + GuidA, e.Id);
                var list = Hf.ReadMods(f.Install);
                Assert.Contains(list.Mods, m => m.Id == "pack." + GuidA);
                Assert.Equal(e.Folder, Placed.Of(Placed.Load(f.Home, f.Install), lib).Folder);
                // placing it again does nothing
                Assert.Equal(e.Folder, Placed.Put(f.Home, f.Install, "Super Holo HUD", lib, list).Folder);
                Assert.Single(Directory.GetDirectories(f.Mods));
            }
        }

        [Fact]
        public void PutRefusesAModHellForgeHasFromElsewhere()
        {
            using (var f = new FakeHellForge())
            {
                // the player's own copy (a port staged by hand): same Guid, not placed by the plugin
                var own = Path.Combine(f.Mods, "Super Holo HUD v1.226.1");
                Directory.CreateDirectory(own);
                File.WriteAllText(Path.Combine(own, "manifest.json"), "{\"Guid\": \"" + GuidA + "\", \"Name\": \"Super Holo HUD v1.226.1\"}");
                var before = Directory.GetFiles(own, "*", SearchOption.AllDirectories).Length;
                var lib = f.LibraryPack("Super Holo HUD", GuidA);
                var r = Assert.Throws<Placed.Refusal>(() => Placed.Put(f.Home, f.Install, "Super Holo HUD", lib, Hf.ReadMods(f.Install)));
                Assert.Equal("same_id", r.Key);
                Assert.Equal("Super Holo HUD v1.226.1", r.Arg);
                Assert.Single(Directory.GetDirectories(f.Mods));                       // nothing added
                Assert.Equal(before, Directory.GetFiles(own, "*", SearchOption.AllDirectories).Length);
                Assert.Empty(Placed.Load(f.Home, f.Install));
                Assert.Contains("Super Holo HUD v1.226.1", Plugin.Refused("zh-CN", r));
            }
        }

        [Fact]
        public void PutRefusesAFolderHellForgeWouldNotTake()
        {
            using (var f = new FakeHellForge())
            {
                var dir = Path.Combine(f.Library, "Nested only");
                Directory.CreateDirectory(Path.Combine(dir, "deep"));
                File.WriteAllBytes(Path.Combine(dir, "deep", "0123456789abcdef.patch_0"), FakeHellForge.Patch());
                var r = Assert.Throws<Placed.Refusal>(() => Placed.Put(f.Home, f.Install, "Nested only", dir, Hf.ReadMods(f.Install)));
                Assert.Equal("not_a_mod", r.Key);
                Assert.Empty(Directory.GetDirectories(f.Mods));
            }
        }

        [Fact]
        public void TwoModsOfOneNameGetTwoFolders()
        {
            using (var f = new FakeHellForge())
            {
                var a = f.LibraryPack("Same", "11111111-1111-1111-1111-111111111111");
                var bDir = Path.Combine(f.Library, "Same_ffffffffffff");
                Directory.CreateDirectory(bDir);
                File.WriteAllText(Path.Combine(bDir, "manifest.json"), "{\"Guid\": \"22222222-2222-2222-2222-222222222222\", \"Name\": \"Same\", \"Options\": []}");
                File.WriteAllBytes(Path.Combine(bDir, "0123456789abcdef.patch_0"), FakeHellForge.Patch());
                var ea = Placed.Put(f.Home, f.Install, "Same", a, Hf.ReadMods(f.Install));
                var eb = Placed.Put(f.Home, f.Install, "Same", bDir, Hf.ReadMods(f.Install));
                Assert.Equal("Same", ea.Folder);
                Assert.Equal("Same (2)", eb.Folder);
                Assert.Equal("mod", Placed.FolderName("..."));
                Assert.Equal("a b", Placed.FolderName(".a:b"));
            }
        }

        [Fact]
        public void RemoveTakesOutOnlyWhatWasPlacedAndNeverTheLibrary()
        {
            using (var f = new FakeHellForge())
            {
                var lib = f.LibraryPack("Super Holo HUD", GuidA);
                var libPatch = Directory.GetFiles(lib, "*.patch_0", SearchOption.AllDirectories)[0];
                var bytes = File.ReadAllBytes(libPatch);
                var e = Placed.Put(f.Home, f.Install, "Super Holo HUD", lib, Hf.ReadMods(f.Install));
                Placed.Remove(f.Home, f.Install, e);
                Assert.False(Directory.Exists(Path.Combine(f.Mods, e.Folder)));
                Assert.Contains(f.Calls, c => c[0] == "mod-remove" && c[2] == e.Id);
                Assert.Equal(bytes, File.ReadAllBytes(libPatch));
                Assert.Equal(1, FakeHellForge.Links(libPatch));
                Assert.Empty(Placed.Load(f.Home, f.Install));
                // a mod it did not place cannot be removed through it
                Assert.Throws<HfError>(() => Placed.Remove(f.Home, f.Install, new Placed.Entry { Folder = "Someone else's", Id = "x" }));
            }
        }

        [Fact]
        public void RemoveIsRefusedWhileASessionRuns()
        {
            using (var f = new FakeHellForge())
            {
                var lib = f.LibraryPack("Super Holo HUD", GuidA);
                var e = Placed.Put(f.Home, f.Install, "Super Holo HUD", lib, Hf.ReadMods(f.Install));
                f.Running = true;
                Assert.Throws<HfError>(() => Placed.Remove(f.Home, f.Install, e));
                Assert.True(Directory.Exists(Path.Combine(f.Mods, e.Folder)));
                Assert.Single(Placed.Load(f.Home, f.Install));
                // Start changes nothing while it runs
                File.Delete(Directory.GetFiles(lib, "*.patch_0", SearchOption.AllDirectories)[0]);
                Assert.Empty(Plugin.Sync(f.Home, f.Install));
            }
        }

        [Fact]
        public void ALibraryUpdateIsBroughtOver()
        {
            using (var f = new FakeHellForge())
            {
                var lib = f.LibraryPack("Super Holo HUD", GuidA);
                var e = Placed.Put(f.Home, f.Install, "Super Holo HUD", lib, Hf.ReadMods(f.Install));
                Assert.False(Placed.Stale(e));
                // the manager replaces a file (it never writes one in place): new bytes, a new file
                var libPatch = Directory.GetFiles(lib, "*.patch_0", SearchOption.AllDirectories)[0];
                File.Delete(libPatch);
                File.WriteAllBytes(libPatch, FakeHellForge.Patch((Scan.Lua, Encoding.UTF8.GetBytes("print(1)"))));
                File.WriteAllText(Path.Combine(lib, "README.txt"), "new");
                Assert.True(Placed.Stale(Placed.Load(f.Home, f.Install).Values.Single()));
                var said = Plugin.Sync(f.Home, f.Install);
                Assert.Single(said);
                var placedPatch = Path.Combine(f.Mods, e.Folder, "Main", "0123456789abcdef.patch_0");
                Assert.Equal(File.ReadAllBytes(libPatch), File.ReadAllBytes(placedPatch));
                Assert.True(File.Exists(Path.Combine(f.Mods, e.Folder, "README.txt")));
                Assert.False(Placed.Stale(Placed.Load(f.Home, f.Install).Values.Single()));
                // a folder removed in HellForge.exe is forgotten
                Directory.Delete(Path.Combine(f.Mods, e.Folder), true);
                Plugin.Sync(f.Home, f.Install);
                Assert.Empty(Placed.Load(f.Home, f.Install));
            }
        }

        [Fact]
        public void SyncHandsItsListOnOnlyWhenNothingChangedAfterIt()
        {
            using (var f = new FakeHellForge())
            {
                var lib = f.LibraryPack("Super Holo HUD", GuidA);
                var e = Placed.Put(f.Home, f.Install, "Super Holo HUD", lib, Hf.ReadMods(f.Install));
                var status = Hf.ReadStatus(f.Install);
                // nothing to bring over: the one mods read is handed on, status not read again
                int statusReads = f.Calls.Count(c => c[0] == "status"), modsReads = f.Calls.Count(c => c[0] == "mods");
                Assert.Empty(Plugin.Sync(f.Home, f.Install, null, status, out var listRead));
                Assert.NotNull(listRead);
                Assert.Contains(Hf.ParseMods(listRead).Mods, m => m.Id == e.Id);
                Assert.Equal(statusReads, f.Calls.Count(c => c[0] == "status"));
                Assert.Equal(modsReads + 1, f.Calls.Count(c => c[0] == "mods"));
                // something brought over: no list handed on (the caller reads it after the change)
                var libPatch = Directory.GetFiles(lib, "*.patch_0", SearchOption.AllDirectories)[0];
                File.Delete(libPatch);
                File.WriteAllBytes(libPatch, FakeHellForge.Patch((Scan.Lua, Encoding.UTF8.GetBytes("print(2)"))));
                Assert.Single(Plugin.Sync(f.Home, f.Install, null, status, out listRead));
                Assert.Null(listRead);
            }
        }

        // ---------------------------------------------------------------- only affects me

        [Fact]
        public void CoopIsAddedAndTakenOutByteForByte()
        {
            var original = FakeHellForge.Manifest;
            var on = Coop.With(original, "per-player");
            Assert.Equal("per-player", Coop.ValueIn(on));
            Assert.StartsWith("{\r\n  \"coop\": \"per-player\",\r\n  \"id\"", on);
            Assert.Equal(original, Coop.With(on, null));
            // a value it had is replaced, and put back
            var shared = original.Replace("\"version\": \"1.0.0\"", "\"version\": \"1.0.0\",\r\n  \"coop\": \"shared\"");
            Assert.Equal("per-player", Coop.ValueIn(Coop.With(shared, "per-player")));
            Assert.Equal(shared, Coop.With(Coop.With(shared, "per-player"), "shared"));
            // the last key taken out leaves valid JSON
            var gone = Coop.With(shared, null);
            Assert.Null(Coop.ValueIn(gone));
            Assert.NotNull(Json.Obj(gone));
            // one line
            Assert.Equal("per-player", Coop.ValueIn(Coop.With("{\"id\":\"a\"}", "per-player")));
        }

        [Fact]
        public void OnlyMeChangesHellForgesZipAndComesBackAfterAnUpdate()
        {
            using (var f = new FakeHellForge())
            {
                var zip = f.CodeZip();
                var mod = Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "someone.big-pack");
                Assert.Equal("gameplay", mod.Group);
                Assert.True(Coop.Applies(mod));
                Coop.Set(f.Home, f.Install, mod, true);
                mod = Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "someone.big-pack");
                Assert.Equal("per-player", mod.Group);
                Assert.True(Coop.IsPerPlayer(mod));
                Assert.Single(Directory.GetFiles(Path.Combine(f.Home, "hellforge", "backups"), "*.zip"));
                using (var a = ZipFile.OpenRead(zip)) Assert.Equal(2, a.Entries.Count);      // the rest of the zip kept

                // an update brings the author's zip back, without the mark: Start puts it back
                System.Threading.Thread.Sleep(20);
                f.CodeZip();
                Assert.Equal("gameplay", Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "someone.big-pack").Group);
                var said = Plugin.Sync(f.Home, f.Install);
                Assert.Contains(said, s => s.Contains("Big Pack"));
                Assert.Equal("per-player", Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "someone.big-pack").Group);

                // off: the author's text again
                System.Threading.Thread.Sleep(20);
                Coop.Set(f.Home, f.Install, Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "someone.big-pack"), false);
                using (var a = ZipFile.OpenRead(zip))
                using (var r = new StreamReader(a.GetEntry("hellforge-mod.json").Open()))
                    Assert.Equal(FakeHellForge.Manifest, r.ReadToEnd());
                Assert.Empty(Coop.Choices(f.Home));
            }
        }

        [Fact]
        public void OnlyMeNeverWritesThroughAHardLinkToTheLibrary()
        {
            using (var f = new FakeHellForge())
            {
                var lib = Path.Combine(f.Library, "Big Pack_0123456789ab");
                Directory.CreateDirectory(lib);
                File.WriteAllText(Path.Combine(lib, "hellforge-mod.json"), FakeHellForge.Manifest);
                var e = Placed.Put(f.Home, f.Install, "Big Pack", lib, Hf.ReadMods(f.Install));
                Assert.Equal("someone.big-pack", e.Id);
                var mod = Hf.ReadMods(f.Install).Mods.Single(m => m.Id == e.Id);
                Coop.Set(f.Home, f.Install, mod, true);
                Assert.Equal(FakeHellForge.Manifest, File.ReadAllText(Path.Combine(lib, "hellforge-mod.json")));
                Assert.Equal(1, FakeHellForge.Links(Path.Combine(lib, "hellforge-mod.json")));
                Assert.Equal("per-player", Hf.ReadMods(f.Install).Mods.Single(m => m.Id == e.Id).Group);
                // HellForge's own mods are never offered the switch
                Assert.False(Coop.Applies(new Hf.Mod { Tags = { "code", "bundled" } }));
                Assert.False(Coop.Applies(new Hf.Mod { Tags = { "pack", "lua" } }));
                Assert.False(Coop.Applies(new Hf.Mod { Group = "cosmetic", Tags = { "code" } }));   // never compared
                Assert.True(Coop.Applies(new Hf.Mod { Group = "per-player", Tags = { "code" } }));
            }
        }

        [Fact]
        public void EveryGameplayModOnlyAffectsMeExceptWhatHellForgeRefuses()
        {
            using (var f = new FakeHellForge())
            {
                var free = f.CodeFolder("a.free", "Free Mod");
                var net = f.CodeFolder("b.net", "Net Mod", ",\r\n  \"native\": {\"networkFieldTypes\": [1]}");
                var own = f.CodeFolder("hf.bundled", "HellForge Own", ",\r\n  \"bundledInTest\": true");
                var byHand = f.CodeFolder("c.hand", "By Hand", ",\r\n  \"coop\": \"per-player\"");
                var netText = File.ReadAllText(Path.Combine(net, "hellforge-mod.json"));
                var ownText = File.ReadAllText(Path.Combine(own, "hellforge-mod.json"));

                Assert.False(Coop.AllOn(f.Home));
                Assert.Empty(Coop.MarkAll(f.Home, f.Install).Marked);                    // off: nothing

                var (marked, refused) = Coop.SetAll(f.Home, f.Install, true);
                Assert.Equal(new[] { "Free Mod" }, marked);
                Assert.Single(refused);
                Assert.Contains("Net Mod", refused[0]);
                Assert.Contains("networkFieldTypes", refused[0]);
                var list = Hf.ReadMods(f.Install);
                Assert.Equal("per-player", list.Mods.Single(m => m.Id == "a.free").Group);
                Assert.Equal("gameplay", list.Mods.Single(m => m.Id == "b.net").Group);   // put back as it was
                Assert.Equal(netText, File.ReadAllText(Path.Combine(net, "hellforge-mod.json")));
                Assert.Equal("gameplay", list.Mods.Single(m => m.Id == "hf.bundled").Group);
                Assert.Equal(ownText, File.ReadAllText(Path.Combine(own, "hellforge-mod.json")));
                Assert.Empty(list.Problems);
                Assert.Single(Coop.Refused(f.Home));

                // while it is on, each covered mod's own switch is locked (the user, 2026-11-12):
                // marked ones follow the setting, the refused one shows why; HellForge's own is free
                var refusedNow = Coop.Refused(f.Home);
                Assert.Equal("all", Coop.Lock(true, refusedNow, list.Mods.Single(m => m.Id == "a.free")));
                Assert.Equal("all", Coop.Lock(true, refusedNow, list.Mods.Single(m => m.Id == "c.hand")));
                Assert.Equal("refused", Coop.Lock(true, refusedNow, list.Mods.Single(m => m.Id == "b.net")));
                Assert.Null(Coop.Lock(true, refusedNow, list.Mods.Single(m => m.Id == "hf.bundled")));
                Assert.Null(Coop.Lock(true, refusedNow, new Hf.Mod { Id = "e.look", Group = "cosmetic", Tags = { "code" } }));
                Assert.Null(Coop.Lock(false, refusedNow, list.Mods.Single(m => m.Id == "a.free")));

                // the refused one is not tried again; a mod added later is marked by Start
                f.CodeFolder("d.new", "New Mod");
                var said = Plugin.Sync(f.Home, f.Install);
                Assert.Contains(said, s => s.Contains("New Mod"));
                Assert.DoesNotContain(said, s => s.Contains("Net Mod"));
                Assert.Equal("per-player", Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "d.new").Group);

                // off: the marks it made are taken out; the one marked by hand before stays
                Coop.SetAll(f.Home, f.Install, false);
                list = Hf.ReadMods(f.Install);
                Assert.Equal("gameplay", list.Mods.Single(m => m.Id == "a.free").Group);
                Assert.Equal("gameplay", list.Mods.Single(m => m.Id == "d.new").Group);
                Assert.Equal("per-player", list.Mods.Single(m => m.Id == "c.hand").Group);
                Assert.Equal("{\r\n  \"id\": \"a.free\",\r\n  \"name\": \"Free Mod\"\r\n}\r\n", File.ReadAllText(Path.Combine(free, "hellforge-mod.json")));
                Assert.False(Coop.AllOn(f.Home));
                Assert.Empty(Plugin.Sync(f.Home, f.Install));
            }
        }

        /// <summary>The tags for what a library mod holds, by HellForge's kinds of mod: C (sources
        /// HellForge builds), DLL (prebuilt), STATS; several side by side; none for a data mod.</summary>
        [Fact]
        public void LibraryModsAreTaggedByHellForgesKindsOfMod()
        {
            using (var f = new FakeHellForge())
            {
                string Mod(string name, string code, bool src, bool stats, bool patch = false)
                {
                    var d = Path.Combine(f.Home, "lib", name);
                    Directory.CreateDirectory(d);
                    if (code != null) File.WriteAllText(Path.Combine(d, "hellforge-mod.json"), "{\"id\": \"x." + name + "\", \"name\": \"" + name + "\"" + code + "}");
                    if (src) { Directory.CreateDirectory(Path.Combine(d, "src")); File.WriteAllText(Path.Combine(d, "src", "mod.c"), ""); }
                    if (stats) File.WriteAllText(Path.Combine(d, "hellforge-stats.json"), "{}");
                    if (patch) File.WriteAllText(Path.Combine(d, "9ba626afa44a3aa3.patch_0"), "x");
                    return d;
                }
                const string Code = ", \"code\": {\"dll\": \"m.dll\", \"sdk\": 16}";
                const string Prebuilt = ", \"code\": {\"dll\": \"m.dll\", \"sdk\": 16, \"prebuilt\": true}";
                var host = new Dictionary<string, object> { ["home"] = f.Home, ["lang"] = "en" };
                string Texts(string d) => string.Join(" ", (Plugin.ModTags(d, host) ?? new List<Dictionary<string, object>>()).Select(t => (string)t["text"]));

                Assert.Equal("C", Texts(Mod("built", Code, src: true, stats: false)));
                Assert.Equal("DLL", Texts(Mod("prebuilt", Code, src: false, stats: false)));
                var both = Mod("own-dll", Prebuilt, src: true, stats: false);
                Assert.Equal("DLL", Texts(both));
                Assert.DoesNotContain("not included", (string)Plugin.ModTags(both, host)[0]["tip"]);
                Assert.Equal("STATS", Texts(Mod("stats", "", src: false, stats: true)));
                var mixed = Mod("mixed", Code, src: true, stats: true);
                Assert.Equal("C STATS", Texts(mixed));
                Assert.DoesNotContain("no code", (string)Plugin.ModTags(mixed, host)[1]["tip"]);
                Assert.Equal("", Texts(Mod("data", "", src: false, stats: false, patch: true)));
                Assert.Equal("", Texts(Mod("official", null, src: false, stats: false, patch: true)));
                host["lang"] = "zh-CN";
                Assert.Equal("C 数值", Texts(Path.Combine(f.Home, "lib", "mixed")));
                Assert.Equal("该 MOD 按字段名修改游戏数值，不含代码。", (string)Plugin.ModTags(Path.Combine(f.Home, "lib", "stats"), host)[0]["tip"]);
            }
        }

        // ---------------------------------------------------------------- what goes wrong on HellForge

        [Fact]
        public void ScanFindsRenderConfigStringsAndBootModLoaderCalls()
        {
            using (var f = new FakeHellForge())
            {
                var plain = f.LibraryPack("Plain", GuidA, FakeHellForge.Patch((Scan.Lua, Encoding.UTF8.GetBytes("local x = Mods.get('a')"))));
                var facts = Scan.Of(plain);
                Assert.True(facts.HasLua);
                Assert.False(facts.Any);

                var risky = f.LibraryPack("Risky", "33333333-3333-3333-3333-333333333333", FakeHellForge.Patch(
                    (Scan.RenderConfig, new byte[64]), (Scan.Strings, new byte[16]),
                    (Scan.Lua, Encoding.UTF8.GetBytes("local g = CowboyBingusModLoader . game"))));
                facts = Scan.Of(risky);
                Assert.True(facts.RenderConfig && facts.Strings && facts.BootModLoaderApi);
                Assert.Equal(3, Plugin.Risks("zh-CN", facts).Count);

                // not a patch file: nothing
                File.WriteAllBytes(Path.Combine(plain, "Main", "fedcba9876543210.patch_0"), new byte[] { 1, 2, 3 });
                Assert.False(Scan.Of(plain).Any);
            }
        }

        [Fact]
        public void RowNotesOnlyOnModsInHellForge()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { f.Install };
                try
                {
                    var host = Host(f);
                    Plugin.Choose(f.Home, f.Install);
                    var plain = f.LibraryPack("Plain", GuidA);
                    var risky = f.LibraryPack("Risky", "33333333-3333-3333-3333-333333333333", FakeHellForge.Patch((Scan.RenderConfig, new byte[8])));
                    var other = f.LibraryPack("Elsewhere", "44444444-4444-4444-4444-444444444444", FakeHellForge.Patch((Scan.RenderConfig, new byte[8])));
                    Plugin.Found(f.Home, true);
                    Placed.Put(f.Home, f.Install, "Plain", plain, Hf.ReadMods(f.Install));
                    Placed.Put(f.Home, f.Install, "Risky", risky, Hf.ReadMods(f.Install));
                    Assert.True(Plugin.Start(host) == false);
                    Assert.Equal("Blue", Plugin.ModNote(plain, host)["colour"]);
                    var note = Plugin.ModNote(risky, host);
                    Assert.Equal("Yellow", note["colour"]);
                    Assert.Contains("render", ((string)note["tip"]).ToLowerInvariant());
                    Assert.Null(Plugin.ModNote(other, host));
                }
                finally { Install.PlacesOverride = null; }
            }
        }

        // ---------------------------------------------------------------- sessions

        [Fact]
        public void ACrashIsOnlyWhatTheLoaderOrWindowsRecorded()
        {
            var ended = new Hf.Session { Status = "observed", Completed = "2026-11-10T18:39:56Z", ExitCode = "0x00000001", Crashed = true };
            // 2026-11-10: a normal quit on 25480438 writes dumps and exits with 1; HellForge says crashed
            Assert.Equal(Hf.Ending.EndedWithError, Hf.EndingOf(ended, new Dictionary<string, object> { ["loaderCrash"] = null, ["windowsCrash"] = null }));
            Assert.Equal(Hf.Ending.EndedWithError, Hf.EndingOf(ended, new Dictionary<string, object> { ["windowsCrash"] = "none" }));
            Assert.Equal(Hf.Ending.Crashed, Hf.EndingOf(ended, new Dictionary<string, object> { ["loaderCrash"] = "0xC0000005 game.dll+0x1234" }));
            Assert.Equal(Hf.Ending.Crashed, Hf.EndingOf(ended, new Dictionary<string, object> { ["windowsCrash"] = new Dictionary<string, object> { ["module"] = "game.dll" } }));
            Assert.Equal(Hf.Ending.Ended, Hf.EndingOf(new Hf.Session { Status = "observed", Completed = "x", ExitCode = "0x00000000" }, null));
            Assert.Equal(Hf.Ending.Failed, Hf.EndingOf(new Hf.Session { Status = "failed", Completed = "x" }, null));
            Assert.Equal(Hf.Ending.Running, Hf.EndingOf(new Hf.Session { Status = "observing" }, null));
        }

        [Fact]
        public void StatusModsAndSettingsAreReadAsHellForgePrintsThem()
        {
            // shapes printed by HellForge.Cli 0.4.0-beta on 2026-11-11 (names shortened)
            var status = Hf.ParseStatus(Json.Obj("{\"install\":\"X\",\"gameBuild\":\"25480438\",\"hellforge\":\"0.4.0-beta (d8a8b46)\",\"engine\":\"0.1.47\",\"modSdk\":16," +
                "\"ready\":{\"gameCopy\":true,\"sessionRunner\":true,\"linuxNamespace\":false},\"networkBlock\":\"wfp\"," +
                "\"settings\":{\"profile\":\"default\",\"war\":\"live\",\"sandbox\":true}," +
                "\"session\":{\"running\":false,\"lastRun\":\"X\\\\logs\\\\sessions\\\\7408\",\"lastStatus\":\"observed\",\"lastFailure\":null},\"languages\":[]}"));
            Assert.True(status.GameCopy);
            Assert.False(status.Running);
            Assert.Equal(16, status.ModSdk);
            Assert.Equal("25480438", status.GameBuild);
            var mods = Hf.ParseMods(Json.Obj("{\"mods\":[{\"id\":\"pack.5d6f2a71-3c84-4b19-9f27-8ae410c6b350\",\"name\":\"Hellpod\",\"version\":\"\",\"enabled\":true,\"group\":\"cosmetic\"," +
                "\"tags\":[\"pack\"],\"folder\":\"F\",\"archive\":null,\"removable\":true,\"options\":[{\"name\":\"Sky Blue\",\"on\":true,\"variants\":[\"50\",\"100\"],\"picked\":null}]}," +
                "{\"id\":\"zz.local.stats\",\"name\":\"Local Stats\",\"enabled\":true,\"group\":\"per-player\",\"tags\":[\"stats\",\"local-stats\"],\"options\":null}]," +
                "\"problems\":[],\"modlist\":\"a581e98f\",\"settings\":\"S\"}"));
            Assert.Equal("a581e98f", mods.Modlist);
            Assert.Equal(new[] { "50", "100" }, mods.Mods[0].Options[0].Variants);
            Assert.True(mods.Mods[1].Hidden);
            var settings = Hf.ParseSettings(Json.Obj("{\"profile\":\"default\",\"war\":\"live\",\"multiplayer\":true,\"sandbox\":true,\"steamBridge\":true," +
                "\"playWithStrangers\":true,\"playWithStrangersChosen\":null,\"liveChanges\":true,\"forgeCreditChance\":30}"));
            Assert.True(settings.Strangers);
            Assert.Null(settings.StrangersChosen);
            Assert.Equal(30, settings.ForgeCredits);
            Assert.True(Hf.GoodProfileName("all"));
            Assert.False(Hf.GoodProfileName("All"));
            Assert.False(Hf.GoodProfileName(new string('a', 33)));
        }

        [Fact]
        public void TheSettingsFolderFollowsASandboxedSession()
        {
            var install = @"E:\HF";
            Assert.Equal(@"E:\HF\userdata\environment\Roaming\Arrowhead\Helldivers2", Install.SettingsFolder(install, new Hf.Status()));
            var run = new Hf.Status { Running = true, Sandbox = true, LastRun = @"E:\HF\logs\sessions\r1" };
            Assert.Equal(@"E:\HF\logs\sessions\r1\game\environment\Roaming\Arrowhead\Helldivers2", Install.SettingsFolder(install, run));
            Assert.Equal(@"E:\HF\logs\sessions\r1\game\environment\Local\CowboyBingus\Helldivers2\Logs\BingusSharedLoader.log", Install.BingusLog(install, run.LastRun));
        }

        // ---------------------------------------------------------------- text

        [Fact]
        public void EveryLanguageHasEveryKey()
        {
            var asm = typeof(Plugin).Assembly;
            var files = asm.GetManifestResourceNames().Where(n => n.StartsWith("Lang.")).ToDictionary(n => n, n =>
            {
                using (var r = new StreamReader(asm.GetManifestResourceStream(n)))
                    return new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(r.ReadToEnd());
            });
            Assert.Contains("Lang.en.json", files.Keys);
            Assert.Contains("Lang.zh-CN.json", files.Keys);
            var en = files["Lang.en.json"].Keys.Where(k => !k.EndsWith("_1")).ToList();
            foreach (var f in files)
                Assert.Empty(en.Except(f.Value.Keys));
            // every text the code asks for exists in English
            var src = Path.Combine(Path.GetDirectoryName(asm.Location), "..", "..", "..", "..", "src");
            if (Directory.Exists(src))
                foreach (var cs in Directory.GetFiles(src, "*.cs"))
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(cs), "\\bT\\(\"([a-z_0-9]+)\""))
                        Assert.True(files["Lang.en.json"].ContainsKey(m.Groups[1].Value), m.Groups[1].Value + " in " + Path.GetFileName(cs));
            Assert.Equal("HellForge", Plugin.Title("zh-CN"));
            Assert.NotEqual(Plugin.Summary("en"), Plugin.Summary("zh-CN"));
            Assert.Equal(Plugin.Summary("en"), Plugin.Summary("de"));
        }
    }
}
