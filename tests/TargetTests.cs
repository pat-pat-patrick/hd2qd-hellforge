using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    /// <summary>HellForge as the manager's deploy target: the manager hands the list in use as
    /// host["mods"] (PluginHost.Mods: each enabled mod {id, name, path, files [{src, name}]}).</summary>
    public class TargetTests
    {
        static Dictionary<string, object> Host(FakeHellForge f, Func<List<Dictionary<string, object>>> mods) => new Dictionary<string, object>
        {
            ["api"] = 2L, ["home"] = f.Home, ["lang"] = "en", ["data"] = null, ["mods"] = mods, ["target"] = (Func<string>)(() => "hellforge"),
        };

        /// <summary>A library mod and the files a deploy of it would put in the game, named as there.</summary>
        static Dictionary<string, object> Mod(FakeHellForge f, string id, string name, int patches, string guid = null)
        {
            var dir = Path.Combine(f.Library, name + "_" + id);
            Directory.CreateDirectory(Path.Combine(dir, "A"));
            if (guid != null) File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"Guid\": \"" + guid + "\", \"Name\": \"" + name + "\", \"Options\": [{\"Name\": \"A\", \"Include\": [\"A\"]}]}");
            var files = new List<object>();
            for (int i = 0; i < patches; i++)
            {
                var src = Path.Combine(dir, "A", "0123456789abcde" + i + ".patch_0");
                if (!File.Exists(src)) File.WriteAllBytes(src, FakeHellForge.Patch());
                files.Add(new Dictionary<string, object> { ["src"] = src, ["name"] = "0123456789abcde" + i + ".patch_" + (i + 3) });
            }
            return new Dictionary<string, object> { ["id"] = id, ["name"] = name, ["path"] = dir, ["files"] = files };
        }

        [Fact]
        public void TheListInUseIsDeployedIntoHellForge()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { f.Install };
                try
                {
                    Plugin.Found(f.Home, true);
                    var a = Mod(f, "aaaa1111", "First", 2, "11111111-aaaa-1111-aaaa-111111111111");
                    var b = Mod(f, "bbbb2222", "Second", 1);
                    var list = new List<Dictionary<string, object>> { a, b };
                    var host = Host(f, () => list);

                    var t = Plugin.Target(host);
                    Assert.Equal("hellforge", t["id"]);
                    Assert.True((bool)t["found"]);
                    Assert.Equal("luajit", t["lua"]);                          // HellForge's game is older than the 10-20 update
                    Assert.Equal(2L, Plugin.TargetStatus(host)["pending"]);
                    Assert.All(((Dictionary<string, object>)Plugin.TargetStatus(host)["mods"]).Values, v => Assert.Equal("pending", v));   // each row: not yet there

                    var r = Plugin.TargetDeploy(host);
                    Assert.Empty((List<object>)r["refused"]);
                    Assert.DoesNotContain(f.Calls, c => c[0] == "build-source-mods");       // no sources: nothing to build
                    var mods = Hf.ReadMods(f.Install).Mods;
                    var packA = "pack." + Deployed.GuidOf("aaaa1111");
                    var packB = "pack." + Deployed.GuidOf("bbbb2222");
                    Assert.Contains(mods, m => m.Id == packA && m.Name == "First");
                    Assert.Contains(mods, m => m.Id == packB);
                    // each file at the folder's top under its name in the game, a hard link to the library's
                    var placed = Path.Combine(f.Mods, "First", "0123456789abcde1.patch_4");
                    Assert.True(File.Exists(placed));
                    Assert.Equal(2, FakeHellForge.Links(placed));
                    // the manager's last mod wins; HellForge's top one does
                    Assert.Equal(new[] { packB, packA }, f.Order);
                    Assert.Equal(0L, Plugin.TargetStatus(host)["pending"]);
                    Assert.All(((Dictionary<string, object>)Plugin.TargetStatus(host)["mods"]).Values, v => Assert.Equal("link", v));       // each row: there as hard links to the library's files
                    Assert.Empty(Directory.GetDirectories(f.Mods, ".hd2qd-*"));

                    // the same list again: nothing placed again, and HellForge's slow list read once
                    int reads = f.Calls.Count(c => c[0] == "mods");
                    r = Plugin.TargetDeploy(host);
                    Assert.Contains("0 deployed, 2 unchanged", (string)r["said"]);
                    Assert.Equal(reads + 1, f.Calls.Count(c => c[0] == "mods"));
                    Assert.Equal(0, f.StateCalls);                                          // order and switches: the state file, no mod-order / mod-switch

                    // a switched off in HellForge.exe comes back on; b off in the manager goes out
                    f.SwitchOff(packA);
                    list.Remove(b);
                    Assert.Equal(1L, Plugin.TargetStatus(host)["pending"]);
                    reads = f.Calls.Count(c => c[0] == "mods");
                    r = Plugin.TargetDeploy(host);
                    Assert.Contains("1 removed", (string)r["said"]);
                    Assert.Equal(reads + 1, f.Calls.Count(c => c[0] == "mods"));           // not read again after removing
                    Assert.DoesNotContain(f.Calls, c => c[0] == "mod-remove" || c[0] == "mod-order" || c[0] == "mod-switch");
                    mods = Hf.ReadMods(f.Install).Mods;
                    Assert.DoesNotContain(mods, m => m.Id == packB);
                    Assert.True(mods.Single(m => m.Id == packA).Enabled);
                    Assert.True(Directory.Exists(Path.Combine(f.Library, "Second_bbbb2222")));   // the library is not touched

                    // a library file replaced (an update): placed again
                    var src = Path.Combine(f.Library, "First_aaaa1111", "A", "0123456789abcde0.patch_0");
                    File.Delete(src);
                    File.WriteAllBytes(src, FakeHellForge.Patch((Scan.Lua, new byte[] { 1, 2, 3 })));
                    Assert.Equal(1L, Plugin.TargetStatus(host)["pending"]);
                    Plugin.TargetDeploy(host);
                    Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(Path.Combine(f.Mods, "First", "0123456789abcde0.patch_3")));

                    // purge: everything it put there taken out, nothing else
                    var own = f.CodeFolder("someone.own", "Own Mod");
                    Assert.Contains("1 mod removed", Plugin.TargetPurge(host));
                    Assert.False(Directory.Exists(Path.Combine(f.Mods, "First")));
                    Assert.True(Directory.Exists(own));
                }
                finally { Install.PlacesOverride = null; }
            }
        }

        [Fact]
        public void AModHellForgeHasFromElsewhereIsNotDeployedOverIt()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { f.Install };
                try
                {
                    Plugin.Found(f.Home, true);
                    const string guid = "22222222-bbbb-2222-bbbb-222222222222";
                    var hand = Path.Combine(f.Mods, "Super Holo HUD v1.226.1 port");
                    Directory.CreateDirectory(hand);
                    File.WriteAllText(Path.Combine(hand, "manifest.json"), "{\"Guid\": \"" + guid + "\", \"Name\": \"SHH port\"}");
                    var list = new List<Dictionary<string, object>> { Mod(f, "cccc3333", "Super Holo HUD", 1, guid) };
                    var r = Plugin.TargetDeploy(Host(f, () => list));
                    var refused = ((List<object>)r["refused"]).Cast<string>().ToList();
                    Assert.Single(refused);
                    Assert.Contains("SHH port", refused[0]);
                    Assert.False(Directory.Exists(Path.Combine(f.Mods, "Super Holo HUD")));
                    Assert.True(File.Exists(Path.Combine(hand, "manifest.json")));
                }
                finally { Install.PlacesOverride = null; }
            }
        }

        [Fact]
        public void APacksOwnFilesGoAlongAndAFolderHellForgeCannotNameIsStillTakenOut()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { f.Install };
                try
                {
                    Plugin.Found(f.Home, true);
                    var bsl = Mod(f, "bsl00001", "Bingus Shared Loader", 1, "33333333-cccc-3333-cccc-333333333333");
                    // HellForge knows Bingus Shared Loader by this file (LuaRuntimes)
                    File.WriteAllText(Path.Combine((string)bsl["path"], "BingusSharedLoader-manifest.json"), "{}");
                    var list = new List<Dictionary<string, object>> { bsl };
                    var host = Host(f, () => list);
                    Plugin.TargetDeploy(host);
                    var placed = Path.Combine(f.Mods, "Bingus Shared Loader");
                    Assert.True(File.Exists(Path.Combine(placed, "BingusSharedLoader-manifest.json")));
                    Assert.Contains(Directory.GetFiles(placed), p => p.EndsWith(".patch_3"));
                    Assert.Equal(0L, Plugin.TargetStatus(host)["pending"]);

                    // a folder of ours HellForge no longer lists (it could not read it): deleted when the mod goes
                    foreach (var p in Directory.GetFiles(placed, "*.patch_*")) File.Delete(p);
                    File.WriteAllText(Path.Combine(placed, "manifest.json"), "{\"Name\": \"broken\"}");
                    list.Clear();
                    var r = Plugin.TargetDeploy(host);
                    Assert.False(Directory.Exists(placed));
                    Assert.Contains("1 removed", (string)r["said"]);
                }
                finally { Install.PlacesOverride = null; }
            }
        }

        [Fact]
        public void AHellForgeModOfTheLibraryGoesInWhole()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { f.Install };
                try
                {
                    Plugin.Found(f.Home, true);
                    var dir = Path.Combine(f.Library, "Native_0123");
                    Directory.CreateDirectory(Path.Combine(dir, "src"));
                    File.WriteAllText(Path.Combine(dir, "hellforge-mod.json"), "{\r\n  \"id\": \"someone.native\",\r\n  \"name\": \"Native\"\r\n}\r\n");
                    File.WriteAllText(Path.Combine(dir, "src", "mod.c"), "int x;");
                    var host = Host(f, () => new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["id"] = "nnnn0001", ["name"] = "Native", ["path"] = dir, ["files"] = new List<object>() },
                    });
                    Assert.True(Plugin.TargetClaims(dir, host));
                    Assert.False(Plugin.TargetClaims(f.Library, host));
                    var said = new List<string>();
                    host["progress"] = (Action<string>)(t => { lock (said) said.Add(t); });
                    var r = Plugin.TargetDeploy(host);
                    Assert.Empty((List<object>)r["refused"]);
                    // its sources: compiled during the deploy by HellForge's own build, its progress said
                    Assert.Contains(f.Calls, c => c[0] == "build-source-mods");
                    lock (said) Assert.Contains(said, t => t.StartsWith("Downloading the compiler"));
                    Assert.Contains("source mods built (1)", (string)r["log"]);
                    var placed = Path.Combine(f.Mods, "Native");
                    Assert.True(File.Exists(Path.Combine(placed, "src", "mod.c")));
                    Assert.Equal(2, FakeHellForge.Links(Path.Combine(placed, "hellforge-mod.json")));
                    Assert.False(File.Exists(Path.Combine(placed, "manifest.json")));       // its own id, not a pack's
                    Assert.Contains(Hf.ReadMods(f.Install).Mods, m => m.Id == "someone.native" && m.Group == "gameplay");
                    Assert.Equal(new[] { "someone.native" }, f.Order);                      // in HellForge's state file
                    Assert.Equal(0L, Plugin.TargetStatus(host)["pending"]);

                    // every gameplay mod only affects me: put on again after a deploy, never in the library's file
                    Coop.SetAll(f.Home, f.Install, true);
                    File.AppendAllText(Path.Combine(dir, "src", "mod.c"), "\n");            // a change: placed again
                    Plugin.TargetDeploy(host);
                    Assert.Equal("per-player", Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "someone.native").Group);
                    Assert.DoesNotContain("coop", File.ReadAllText(Path.Combine(dir, "hellforge-mod.json")));
                }
                finally { Install.PlacesOverride = null; }
            }
        }

        [Fact]
        public void AHellForgeModHellForgeHasFromElsewhereIsLeftAlone()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { f.Install };
                try
                {
                    Plugin.Found(f.Home, true);
                    f.CodeZip();                                                   // HellForge holds someone.big-pack as a zip
                    var dir = Path.Combine(f.Library, "Big Pack_0123");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "hellforge-mod.json"), FakeHellForge.Manifest);
                    var host = Host(f, () => new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["id"] = "bbbb0001", ["name"] = "Big Pack", ["path"] = dir, ["files"] = new List<object>() },
                    });
                    var refused = ((List<object>)Plugin.TargetDeploy(host)["refused"]).Cast<string>().ToList();
                    Assert.Contains(refused, x => x.Contains("Big Pack"));
                    Assert.False(Directory.Exists(Path.Combine(f.Mods, "Big Pack")));
                }
                finally { Install.PlacesOverride = null; }
            }
        }

        [Fact]
        public void ADeployIsRefusedWhileASessionRuns()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { f.Install };
                try
                {
                    Plugin.Found(f.Home, true);
                    var list = new List<Dictionary<string, object>> { Mod(f, "dddd4444", "Any", 1) };
                    f.Running = true;
                    Assert.Throws<HfError>(() => Plugin.TargetDeploy(Host(f, () => list)));
                    Assert.True((bool)Plugin.TargetStatus(Host(f, () => list))["running"]);
                    Assert.Empty(Directory.GetDirectories(f.Mods));
                }
                finally { Install.PlacesOverride = null; }
            }
        }
    }
}
