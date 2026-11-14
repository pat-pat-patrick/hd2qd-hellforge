using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    /// <summary>What the settings page adds from HellForge's launcher (the user, 2026-11-15): mod
    /// sync's switches, blocklist and downloads (SyncSettings, mods\hellforge-mods.json as
    /// external-launchers.md documents it), the settings fields read back, the templates' notes,
    /// and the shader cache being built (result.json, as HellForge's ShaderCache.Building).</summary>
    public class SyncTests
    {
        static string ModsFile(FakeHellForge f) => Path.Combine(f.Mods, "hellforge-mods.json");

        [Fact]
        public void ModSyncSettingsAreWrittenAndEveryOtherFieldKept()
        {
            using (var f = new FakeHellForge())
            {
                // no file yet: HellForge's defaults
                var s = SyncSettings.Read(f.Install);
                Assert.True(s.Share);
                Assert.True(s.ListPublic);
                Assert.False(s.ShowEditedStats);
                Assert.Empty(s.Blocked);
                Assert.Equal(0, s.Downloaded);

                File.WriteAllText(ModsFile(f), "{\"format\": 1, \"disabled\": [\"a.b\"], \"order\": [\"c.d\", \"a.b\"], \"packs\": {\"pack.x\": {\"off\": [\"Opt\"]}}, " +
                                                "\"blocked\": [\"bad.one\", \"bad.two\"], \"strangers\": \"off\", \"forgeCreditChance\": 40, \"trusted\": [{\"id\": \"t.t\"}]}");
                SyncSettings.Set(f.Install, "share", false);
                SyncSettings.Set(f.Install, "showEditedStats", true);
                SyncSettings.Unblock(f.Install, "bad.one");
                s = SyncSettings.Read(f.Install);
                Assert.False(s.Share);
                Assert.True(s.ListPublic);
                Assert.True(s.ShowEditedStats);
                Assert.Equal(new[] { "bad.two" }, s.Blocked);
                // must live: every other field as it was
                var d = Json.Obj(File.ReadAllText(ModsFile(f)));
                Assert.Equal(new object[] { "a.b" }, Json.List(d, "disabled"));
                Assert.Equal(new object[] { "c.d", "a.b" }, Json.List(d, "order"));
                Assert.NotNull(Json.Obj(Json.Obj(d, "packs"), "pack.x"));
                Assert.Equal("off", Json.Str(d, "strangers"));
                Assert.Equal(40, Json.Long(d, "forgeCreditChance"));
                Assert.Single(Json.List(d, "trusted"));
                Assert.Empty(Directory.GetFiles(f.Mods, "*.tmp"));
                // must die: a key that is not one of mod sync's switches
                Assert.Throws<ArgumentException>(() => SyncSettings.Set(f.Install, "disabled", true));
            }
        }

        [Fact]
        public void DownloadedModsAreCountedAndDeletedTogether()
        {
            using (var f = new FakeHellForge())
            {
                var store = SyncSettings.Synced(f.Install);
                Directory.CreateDirectory(Path.Combine(store, "a.mod@1111", "sub"));
                File.WriteAllBytes(Path.Combine(store, "a.mod@1111", "sub", "x.bin"), new byte[1048576]);
                Directory.CreateDirectory(Path.Combine(store, "b.mod@2222"));
                File.WriteAllBytes(Path.Combine(store, "b.mod@2222", "y.bin"), new byte[524288]);
                Directory.CreateDirectory(Path.Combine(store, ".partial"));          // a dot folder is no mod
                File.WriteAllBytes(Path.Combine(store, ".partial", "z.bin"), new byte[100]);
                var s = SyncSettings.Read(f.Install);
                Assert.Equal(2, s.Downloaded);
                Assert.Equal(1048576 + 524288, s.DownloadedBytes);
                var own = f.CodeFolder("someone.own", "Own Mod");
                SyncSettings.DeleteDownloads(f.Install);
                Assert.False(Directory.Exists(store));
                Assert.True(Directory.Exists(own));                                   // nothing else in mods\
                Assert.Equal(0, SyncSettings.Read(f.Install).Downloaded);
            }
        }

        [Fact]
        public void TheSettingsPageReadsWhatHellForgePrints()
        {
            var s = Hf.ParseSettings(Json.Obj("{\"profile\": \"default\", \"war\": \"live\", \"localPlayFab\": true, \"multiplayer\": true, \"engineLog\": true, \"crashDump\": false, " +
                                              "\"gameOptions\": \"--use-d3d11\", \"sandbox\": true, \"steamBridge\": false, \"playWithStrangers\": false, \"playWithStrangersChosen\": null}"));
            Assert.True(s.LocalPlayFab);
            Assert.True(s.EngineLog);
            Assert.False(s.CrashDump);
            Assert.Equal("--use-d3d11", s.GameOptions);
            Assert.False(s.SteamBridge);
            Assert.Null(s.StrangersChosen);
            var notes = Hf.TemplateNotes(Json.Obj("{\"profiles\": [], \"templates\": [{\"name\": \"starter\", \"description\": \" A new account. \"}, {\"name\": \"dev\"}]}"));
            Assert.Equal("A new account.", notes["starter"]);
            Assert.False(notes.ContainsKey("dev"));                                    // no description, no note
        }

        [Fact]
        public void TheShaderCacheIsBuiltOnlyWhileTheGameHasNoneAndHasNotReachedTheBackend()
        {
            var run = Path.Combine(Path.GetTempPath(), "hd2qd-hellforge-run-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(run);
            try
            {
                void Result(string json) => File.WriteAllText(Path.Combine(run, "result.json"), json);
                Assert.False(Hf.ShaderCacheBuilding(run));                             // no result.json yet
                Result("{\"status\": \"observing\", \"shaderCache\": {\"atStart\": {\"files\": 0, \"bytes\": 0}}}");
                Assert.True(Hf.ShaderCacheBuilding(run));
                // must die: the game reached the backend, or had a cache at the start
                Result("{\"status\": \"observing\", \"firstBackendRequestSeconds\": 41.5, \"shaderCache\": {\"atStart\": {\"files\": 0}}}");
                Assert.False(Hf.ShaderCacheBuilding(run));
                Result("{\"status\": \"observing\", \"shaderCache\": {\"atStart\": {\"files\": 312, \"bytes\": 9}}}");
                Assert.False(Hf.ShaderCacheBuilding(run));
                Result("{\"status\": \"observing\"}");
                Assert.False(Hf.ShaderCacheBuilding(run));
                Result("not json");
                Assert.False(Hf.ShaderCacheBuilding(run));
            }
            finally { Directory.Delete(run, true); }
        }
    }
}
