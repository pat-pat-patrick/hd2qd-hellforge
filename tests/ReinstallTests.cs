using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    /// <summary>Reinstalling HellForge (the user, 2026-11-16): what the installation keeps for its
    /// player backed up and restored as far as the HellForge then there takes it (Reinstall), and a
    /// mod HellForge has from elsewhere put into the library first (Collect).</summary>
    public class ReinstallTests
    {
        static void Put(string root, string rel, string text = "x")
        {
            var p = Path.Combine(root, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        static void Version(string install, string version, string commit) =>
            Put(install, "app/app-build.json", "{\"version\": \"" + version + "\", \"commit\": \"" + commit + "\"}");

        /// <summary>An installation as a player leaves it: what is theirs, and what HellForge or the
        /// game makes again (the shader cache, the game's temp, the editor's icons, a mod).</summary>
        static void Lived(string install)
        {
            Version(install, "0.4.0-beta", "d8a8b46");
            Put(install, "profiles/default/profile.json", "level 50");
            Put(install, "userdata/profiles/default/saves/user_settings.config", "language = \"cn\"");
            Put(install, "userdata/environment/Roaming/Arrowhead/Helldivers2/user_settings.config", "game");
            Put(install, "userdata/shader_cache/pipeline_library.cache", "cache");
            Put(install, "userdata/environment/Temp/t.tmp", "temp");
            Put(install, "launcher-settings.json", "{}");
            Put(install, "account-snapshot/snap.json", "{}");
            Put(install, "mods/armored-overhaul.ini", "turret=360");
            Put(install, "mods/hellforge-mods.json", "{\"order\": []}");
            Put(install, "mods/.hellforge-stats-editor.lock", "");
            Put(install, "mods/Zipped.zip", "zip");
            Put(install, "mods/hellforge-editor/editor-settings.json", "{\"lang\": \"zh-cn\"}");
            Put(install, "mods/hellforge-editor/presets/mine.json", "{}");
            Put(install, "mods/hellforge-editor/game-icons.bin", "icons");
            Put(install, "mods/zz.local.stats/hellforge-stats.json", "{}");
            Put(install, "mods/Some Mod/hellforge-mod.json", "{}");
        }

        static readonly string[] Theirs =
        {
            "profiles/default/profile.json", "userdata/profiles/default/saves/user_settings.config",
            "userdata/environment/Roaming/Arrowhead/Helldivers2/user_settings.config", "launcher-settings.json", "account-snapshot/snap.json",
            "mods/armored-overhaul.ini", "mods/hellforge-mods.json", "mods/hellforge-editor/editor-settings.json",
            "mods/hellforge-editor/presets/mine.json", "mods/zz.local.stats/hellforge-stats.json",
        };

        static bool Has(string install, string rel) => File.Exists(Path.Combine(install, rel.Replace('/', '\\')));

        /// <summary>A fresh installation of that version, as setup leaves it.</summary>
        static void Fresh(string install, string version, string commit)
        {
            Directory.Delete(install, true);
            Directory.CreateDirectory(Path.Combine(install, "mods"));
            Version(install, version, commit);
        }

        [Fact]
        public void TheSameVersionGetsEverythingTheBackupTook()
        {
            using (var f = new FakeHellForge())
            {
                Lived(f.Install);
                var b = Reinstall.Make(f.Home, f.Install, new[] { "Some Mod" });
                Assert.Equal("0.4.0-beta", b.Version);
                Assert.StartsWith(Reinstall.Root(f.Home), b.Folder);                         // outside HellForge: the uninstall cannot take it

                Fresh(f.Install, "0.4.0-beta", "d8a8b46");
                var listed = Reinstall.List(f.Home).Single();
                Assert.Equal(new[] { "Some Mod" }, listed.NotInLibrary);
                var o = Reinstall.Restore(f.Install, listed);
                Assert.Equal(Reinstall.Kind.Same, o.Kind);
                Assert.All(Theirs, rel => Assert.True(Has(f.Install, rel), rel));
                Assert.Equal(Theirs.Length, o.Restored);
                Assert.Empty(o.LeftOut);
                // must die: what HellForge or the game makes again, and the mods, are not in a backup
                foreach (var rel in new[] { "userdata/shader_cache/pipeline_library.cache", "userdata/environment/Temp/t.tmp", "mods/.hellforge-stats-editor.lock",
                                            "mods/Zipped.zip", "mods/hellforge-editor/game-icons.bin", "mods/Some Mod/hellforge-mod.json" })
                    Assert.False(Has(f.Install, rel), rel);
            }
        }

        [Fact]
        public void ANewerVersionGetsAllButTheModListAndAnOlderOneOnlyTheModsSettings()
        {
            using (var f = new FakeHellForge())
            {
                Lived(f.Install);
                var b = Reinstall.Make(f.Home, f.Install, new string[0]);

                Fresh(f.Install, "0.5.0-nightly.20261115", "ba81dbb");
                var o = Reinstall.Restore(f.Install, b);
                Assert.Equal(Reinstall.Kind.Newer, o.Kind);
                Assert.True(Has(f.Install, "profiles/default/profile.json"));
                Assert.False(Has(f.Install, "mods/hellforge-mods.json"));                  // names that version's own mods
                Assert.Equal(new[] { "mods/hellforge-mods.json" }, o.LeftOut);

                // a newer backup into an older HellForge: its profiles may not be readable there
                var newer = Reinstall.Make(f.Home, f.Install, new string[0]);
                Fresh(f.Install, "0.4.0-beta", "d8a8b46");
                o = Reinstall.Restore(f.Install, newer);
                Assert.Equal(Reinstall.Kind.Older, o.Kind);
                Assert.True(Has(f.Install, "mods/armored-overhaul.ini"));                   // a mod's own settings
                Assert.False(Has(f.Install, "profiles/default/profile.json"));
                Assert.False(Has(f.Install, "userdata/profiles/default/saves/user_settings.config"));
                Assert.Contains("profiles/", o.LeftOut);
            }
        }

        [Fact]
        public void AFileARestoreReplacesIsKeptBesideTheBackup()
        {
            using (var f = new FakeHellForge())
            {
                Lived(f.Install);
                var b = Reinstall.Make(f.Home, f.Install, new string[0]);
                Put(f.Install, "profiles/default/profile.json", "level 1, made since");
                var o = Reinstall.Restore(f.Install, b);
                Assert.Equal("level 50", File.ReadAllText(Path.Combine(f.Install, "profiles", "default", "profile.json")));
                Assert.Equal("level 1, made since", File.ReadAllText(Path.Combine(o.Replaced, "profiles", "default", "profile.json")));
            }
        }

        [Fact]
        public void VersionsAreComparedOnTheSafeSide()
        {
            var b = new Reinstall.Backup { Version = "0.4.0-beta", Commit = "d8a8b46" };
            Assert.Equal(Reinstall.Kind.Same, Reinstall.Compare(b, "0.4.0-beta", "d8a8b46"));
            Assert.Equal(Reinstall.Kind.Newer, Reinstall.Compare(b, "0.5.0-nightly.20261115", "ba81dbb"));
            Assert.Equal(Reinstall.Kind.Older, Reinstall.Compare(b, "0.3.0-beta", "b1aef77"));
            Assert.Equal(Reinstall.Kind.Older, Reinstall.Compare(b, "0.4.0-dev.12", "caea938"));       // the same numbers, another build
            Assert.Equal(Reinstall.Kind.Older, Reinstall.Compare(b, null, null));                     // not read
        }

        // ---------------------------------------------------------------- into the library

        /// <summary>The manager's side of "add_to_library": a copy into the library folder, appended
        /// to the list in use, on or off as asked.</summary>
        static Dictionary<string, object> Host(FakeHellForge f, List<Dictionary<string, object>> list, string target = "hellforge", bool canAdd = true)
        {
            var host = new Dictionary<string, object>
            {
                ["api"] = 2L, ["home"] = f.Home, ["lang"] = "en", ["data"] = null, ["target"] = (Func<string>)(() => target),
                ["mods"] = (Func<List<Dictionary<string, object>>>)(() => list.Where(m => (bool)m["on"]).ToList()),
            };
            if (canAdd)
                host["add_to_library"] = (Func<string, string, bool, string>)((path, forTarget, enabled) =>
                {
                    Assert.Equal("hellforge", forTarget);
                    var to = Path.Combine(f.Library, Path.GetFileName(path));
                    if (Directory.Exists(to)) throw new InvalidOperationException("already in the library folder: " + to);
                    Directory.CreateDirectory(to);
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var dest = Path.Combine(to, file.Substring(path.Length + 1));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(file, dest);
                    }
                    var id = Guid.NewGuid().ToString();
                    list.Add(new Dictionary<string, object> { ["id"] = id, ["name"] = Path.GetFileName(path), ["path"] = to, ["files"] = new List<object>(), ["on"] = enabled });
                    return id;
                });
            return host;
        }

        [Fact]
        public void AModHellForgeHasFromElsewhereGoesIntoTheLibraryAndComesBackFromIt()
        {
            using (var f = new FakeHellForge())
            {
                Install.PlacesOverride = () => new[] { f.Install };
                try
                {
                    Plugin.Found(f.Home, true);
                    var hand = f.CodeFolder("someone.hand", "By Hand");
                    Directory.CreateDirectory(Path.Combine(hand, "src"));
                    File.WriteAllText(Path.Combine(hand, "src", "mod.c"), "int x;");
                    var list = new List<Dictionary<string, object>>();
                    var host = Host(f, list);
                    Coop.MarkAll(f.Home, f.Install);                                         // every gameplay mod only for me, on by default
                    var mod = Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "someone.hand");
                    Assert.Equal("per-player", mod.Group);
                    Assert.True(Collect.Offered(mod, false));
                    Assert.Null(Collect.Blocked(host));

                    var r = Collect.Run(host, f.Home, f.Install, mod);
                    Assert.Empty((List<object>)r["refused"]);
                    var copy = Path.Combine(f.Library, "By Hand");
                    // the library holds the author's text: the mark is HellForge's copy's only
                    Assert.DoesNotContain("coop", File.ReadAllText(Path.Combine(copy, "hellforge-mod.json")));
                    var after = Hf.ReadMods(f.Install).Mods.Single(m => m.Id == "someone.hand");
                    Assert.Equal(2, FakeHellForge.Links(Path.Combine(after.Folder, "src", "mod.c")));   // placed from the library, a hard link
                    Assert.Equal(new[] { "someone.hand" }, Deployed.Load(f.Home, f.Install).Values.Select(e => e.PackId));
                    Assert.True(Coop.IsPerPlayer(after));                                    // and marked again after the deploy
                }
                finally { Install.PlacesOverride = null; }
            }
        }

        [Fact]
        public void HellForgesOwnModsAndAnOldManagerAreNeverOffered()
        {
            using (var f = new FakeHellForge())
            {
                Assert.False(Collect.Offered(new Hf.Mod { Id = "hellforge.editor", Folder = "x", Tags = { "code", "bundled" } }, false));
                Assert.False(Collect.Offered(new Hf.Mod { Id = "zz.local.stats", Folder = "x", Tags = { "stats", "local-stats" } }, false));
                Assert.False(Collect.Offered(new Hf.Mod { Id = "a.b", Folder = "x", Tags = { "code" } }, true));    // the deploy placed it
                Assert.Equal("collect_needs_newer", Collect.Blocked(Host(f, new List<Dictionary<string, object>>(), canAdd: false)));
                Assert.Equal("collect_needs_target", Collect.Blocked(Host(f, new List<Dictionary<string, object>>(), target: "official")));
            }
        }
    }
}
