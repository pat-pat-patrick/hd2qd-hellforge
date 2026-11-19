using System;
using System.IO;
using System.Text;
using System.Threading;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    /// <summary>Licensed mods on HellForge (0.15.0, the user 2026-11-17: "接入授权"): the player's
    /// licenses and online answers relayed into HellForge's sandboxed AppData and the sessions
    /// that ask; a session's question relayed out to the real AppData.</summary>
    public class LicenseRelayTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "hfrelay_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        readonly string appData, install, realDir, sandboxDir, sessionDir;
        const string Issuer = "license.patpatpatrick.com";

        public LicenseRelayTests()
        {
            appData = Path.Combine(root, "Roaming");
            install = Path.Combine(root, "HellForge");
            realDir = LicenseRelay.RealDir(appData);
            sandboxDir = LicenseRelay.SandboxDir(install);
            sessionDir = Path.Combine(install, "logs", "sessions", "abc123", "game", "environment", "Roaming", "Arrowhead", "Helldivers2", "licenses");
            Directory.CreateDirectory(realDir);
            Directory.CreateDirectory(Path.Combine(install, "logs", "sessions"));
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        static void Write(string dir, string name, string text)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
        }

        static string Read(string dir, string name) => File.Exists(Path.Combine(dir, name)) ? File.ReadAllText(Path.Combine(dir, name)) : null;

        static bool Within(Func<bool> done, int seconds = 10)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until) { if (done()) return true; Thread.Sleep(50); }
            return done();
        }

        [Fact]
        public void Licenses_and_answers_are_pushed_questions_are_not_and_equal_files_are_left()
        {
            Write(realDir, Issuer + ".txt", "HD2 MOD LICENSE 1\nlicense\n");
            Write(realDir, Issuer + ".attest", "HD2 MOD ATTEST 1\nanswer\n");
            Write(realDir, Issuer + ".ask", "HD2 MOD ASK 1\nofficial game's\n");
            Assert.Equal(2, LicenseRelay.Push(realDir, sandboxDir, false));
            Assert.Equal("HD2 MOD LICENSE 1\nlicense\n", Read(sandboxDir, Issuer + ".txt"));
            Assert.Equal("HD2 MOD ATTEST 1\nanswer\n", Read(sandboxDir, Issuer + ".attest"));
            Assert.Null(Read(sandboxDir, Issuer + ".ask"));
            Assert.Equal(0, LicenseRelay.Push(realDir, sandboxDir, false));                     // nothing differs
        }

        [Fact]
        public void A_sessions_question_goes_out_and_its_answer_and_license_come_back()
        {
            Write(realDir, Issuer + ".txt", "HD2 MOD LICENSE 1\nfirst\n");
            LicenseRelay.Start(appData, install);
            Assert.Equal("HD2 MOD LICENSE 1\nfirst\n", Read(sandboxDir, Issuer + ".txt"));   // a session starts with it

            // the game in a session asks (HellForge's Steam ID account)
            Write(sessionDir, Issuer + ".ask", "HD2 MOD ASK 1\nnonce=" + new string('a', 32) + "\naccount=0110000100000001\n");
            Assert.True(Within(() => Read(realDir, Issuer + ".ask")?.Contains("account=0110000100000001") == true), "the question reaches the real AppData");
            Assert.True(Within(() => Read(sessionDir, Issuer + ".txt") == "HD2 MOD LICENSE 1\nfirst\n"), "the session gets the license");

            // the Member licenses plugin answers there: a new license (the account bound) and the answer
            Write(realDir, Issuer + ".txt", "HD2 MOD LICENSE 1\nnaming both\n");
            Write(realDir, Issuer + ".attest", "HD2 MOD ATTEST 1\nnonce=" + new string('a', 32) + "\n");
            Assert.True(Within(() => Read(sessionDir, Issuer + ".attest")?.Contains(new string('a', 32)) == true
                                    && Read(sessionDir, Issuer + ".txt") == "HD2 MOD LICENSE 1\nnaming both\n"), "both reach the running session");
            Assert.True(Within(() => Read(sandboxDir, Issuer + ".txt") == "HD2 MOD LICENSE 1\nnaming both\n"), "and the sandbox, for the next session");
        }

        [Fact]
        public void A_sign_out_takes_the_license_away_from_the_sandbox_and_a_running_session()
        {
            // the user, 2026-11-17: signed out in the manager, and the beacon on HellForge stayed lit
            Write(realDir, Issuer + ".txt", "HD2 MOD LICENSE 1\nlicense\n");
            Write(realDir, Issuer + ".attest", "HD2 MOD ATTEST 1\nanswer\n");
            Write(sessionDir, Issuer + ".ask", "HD2 MOD ASK 1\nthe session's\n");    // a session that asked before the manager started
            LicenseRelay.Start(appData, install);
            Assert.True(Within(() => Read(sessionDir, Issuer + ".txt") != null), "the running session gets the license at the start");
            File.Delete(Path.Combine(realDir, Issuer + ".txt"));                      // signed out
            Assert.True(Within(() => Read(sandboxDir, Issuer + ".txt") == null && Read(sessionDir, Issuer + ".txt") == null),
                "the license is gone from the sandbox and the session");
            Assert.NotNull(Read(sandboxDir, Issuer + ".attest"));                     // the answer stays, as in the real AppData
            Assert.NotNull(Read(sessionDir, Issuer + ".ask"));                        // and the game's own question
        }

        [Fact]
        public void A_question_outside_a_licenses_folder_is_not_relayed()
        {
            LicenseRelay.Start(appData, install);
            var stray = Path.Combine(install, "logs", "sessions", "abc123", "game");
            Write(stray, Issuer + ".ask", "HD2 MOD ASK 1\nstray\n");
            LicenseRelay.Relay(Path.Combine(stray, Issuer + ".ask"));
            Assert.Null(Read(realDir, Issuer + ".ask"));
        }
            // ------------------------------------------------ member content (Member licenses 1.7.0)

        string SegDir(string licenses) => Path.Combine(licenses, Issuer, "segments", "super-beacon", "v0.36.0");

        string NewSession(string id, int minutesAgo = 0)
        {
            var folder = Path.Combine(install, "logs", "sessions", id);
            var lic = Path.Combine(folder, "game", "environment", "Roaming", "Arrowhead", "Helldivers2", "licenses");
            Directory.CreateDirectory(folder);
            if (minutesAgo > 0) Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddMinutes(-minutesAgo));
            Directory.CreateDirectory(lic);
            return lic;
        }

        [Fact]
        public void Push_carries_only_the_plaintext_of_segments_and_without_plain_takes_it_away()
        {
            var seg = SegDir(realDir);
            Write(seg, "seg1.lua", "plain 1");
            Write(seg, "seg1.enc", "cipher");
            Write(seg, "receipt.txt", "HD2 MOD SEGMENTS 2\n");
            Write(seg, "key.dpapi", "dpapi");
            Write(seg, "seg1.lua.new-ab12", "half written");
            Assert.Equal(1, LicenseRelay.Push(realDir, sessionDir, true));
            Assert.Equal("plain 1", Read(SegDir(sessionDir), "seg1.lua"));
            foreach (var n in new[] { "seg1.enc", "receipt.txt", "key.dpapi", "seg1.lua.new-ab12" })
                Assert.Null(Read(SegDir(sessionDir), n));                              // never relayed
            Assert.Null(Read(sessionDir, "receipt.txt"));                              // a receipt is no license
            Assert.Equal(1, LicenseRelay.Push(realDir, sessionDir, false));            // not the running session
            Assert.Null(Read(SegDir(sessionDir), "seg1.lua"));
            Assert.NotNull(Read(seg, "seg1.lua"));                                     // the real one untouched
        }

        [Fact]
        public void The_new_session_gets_the_plaintext_while_the_game_runs_and_loses_it_when_it_stops_the_sandbox_never()
        {
            Write(realDir, Issuer + ".txt", "HD2 MOD LICENSE 1\nlicense\n");
            LicenseRelay.Start(appData, install);
            var lic = NewSession("fresh01");                                            // HellForge starts a session
            Thread.Sleep(300);
            Write(SegDir(realDir), "seg1.lua", "plain 1");                             // the game started: decrypted
            Write(SegDir(realDir), "seg2.lua", "plain 2");
            Assert.True(Within(() => Read(SegDir(lic), "seg1.lua") == "plain 1" && Read(SegDir(lic), "seg2.lua") == "plain 2"), "the running session reads the plaintext");
            Assert.True(Within(() => Read(lic, Issuer + ".txt") != null), "and its license");
            Assert.Null(Read(SegDir(sandboxDir), "seg1.lua"));                         // never into userdata\environment
            File.Delete(Path.Combine(SegDir(realDir), "seg1.lua"));                     // the game stopped: removed
            File.Delete(Path.Combine(SegDir(realDir), "seg2.lua"));
            Assert.True(Within(() => Read(SegDir(lic), "seg1.lua") == null && Read(SegDir(lic), "seg2.lua") == null), "the session's plaintext goes with it");
        }

        [Fact]
        public void Plaintext_HellForge_carries_back_into_the_sandbox_is_deleted()
        {
            LicenseRelay.Start(appData, install);
            Write(SegDir(sandboxDir), "seg1.lua", "carried back at a session's end");
            Write(SegDir(sandboxDir), "seg1.enc", "cipher");
            Assert.True(Within(() => Read(SegDir(sandboxDir), "seg1.lua") == null), "the plaintext is gone from userdata\environment");
            Assert.NotNull(Read(SegDir(sandboxDir), "seg1.enc"));                      // only plaintext is touched
        }

        [Fact]
        public void An_old_session_never_becomes_the_running_one_and_its_left_plaintext_is_cleared_at_start()
        {
            var old = NewSession("old01", minutesAgo: 60 * 24 * 3);
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
            Write(SegDir(old), "seg1.lua", "left by a crash");
            LicenseRelay.Start(appData, install);
            Assert.Null(Read(SegDir(old), "seg1.lua"));                                // cleared at the start

            var ended = NewSession("ended01", minutesAgo: 30);                         // ended half an hour ago
            Write(ended, "something.log", "a file created in it");
            Thread.Sleep(300);
            Write(SegDir(realDir), "seg1.lua", "plain 1");                             // the official game starts
            Assert.True(Within(() => Read(sandboxDir, Issuer + ".txt") == null && Read(SegDir(realDir), "seg1.lua") != null));
            Thread.Sleep(1000);
            Assert.Null(Read(SegDir(ended), "seg1.lua"));                              // not a running HellForge session
            Assert.Null(Read(SegDir(old), "seg1.lua"));
        }
    }
}
