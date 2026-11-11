using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    public class LobbyTests
    {
        /// <summary>The example of external-launchers.md ("GET /hellforge/v1/lobbies"), with a second
        /// lobby a fix would let in.</summary>
        const string Sample = "{\"version\": 1, \"available\": true, \"searchedUtc\": \"2026-10-30T20:01:02.0000000Z\"," +
            " \"lobbies\": [{\"lobbyId\": \"6f1c2d3e-0000-4000-8000-000000000001\", \"host\": \"76561198000000000\", \"hostName\": \"Helldiver\", \"friend\": false," +
            " \"source\": \"directory\", \"members\": 1, \"maxMembers\": 4, \"locked\": false, \"privacy\": 0, \"difficulty\": 7, \"planetId\": 168, \"race\": 2," +
            " \"inMission\": true, \"older\": false, \"players\": [\"Helldiver\"], \"joinable\": false, \"verdict\": \"unjoinable\", \"reason\": \"It needs mods installed by hand.\"," +
            " \"downloadBytes\": 0, \"mods\": [{\"id\": \"author.mod\", \"name\": \"Mod\", \"version\": \"1.0\", \"tier\": \"shared\", \"class\": \"code\", \"action\": \"manual\"," +
            " \"why\": \"stranger code, not verified\", \"url\": \"https://example.com/mod\"}]}," +
            " {\"lobbyId\": \"6f1c2d3e-0000-4000-8000-000000000002\", \"hostName\": \"Other\", \"members\": 2, \"maxMembers\": 4, \"difficulty\": null, \"planetId\": null," +
            " \"race\": null, \"joinable\": false, \"verdict\": \"unjoinable\", \"fix\": \"strangers\", \"mods\": []}]," +
            " \"needed\": [{\"id\": \"author.mod\", \"name\": \"Mod\", \"lobbies\": 1, \"versions\": [\"1.0\"], \"url\": \"https://example.com/mod\"}]}";

        [Fact]
        public void TheListIsReadAsTheDocsPrintIt()
        {
            var l = Lobbies.Parse(Json.Obj(Sample));
            Assert.True(l.Available);
            Assert.Equal(2, l.Lobbies.Count);
            var a = l.Lobbies[0];
            Assert.Equal("Helldiver", a.HostName);
            Assert.Equal(7, a.Difficulty);
            Assert.Equal(168, a.PlanetId);
            Assert.Equal("race_terminids", Lobbies.RaceKey(a.Race));
            Assert.Equal("manual", a.Mods[0].Action);
            Assert.Equal("shared", a.Mods[0].Tier);
            var b = l.Lobbies[1];
            Assert.Null(b.Difficulty);
            Assert.Null(b.PlanetId);
            Assert.Null(Lobbies.RaceKey(b.Race));
            Assert.Equal("strangers", b.Fix);
            Assert.Single(l.Needed);
            Assert.Equal(1, l.Needed[0].Lobbies);
            Assert.Equal("1.0 MB", Lobbies.Size(1 << 20));
        }

        [Fact]
        public void TheBackendIsFoundInTheRunsReadyFile()
        {
            var run = Path.Combine(Path.GetTempPath(), "hd2qd-hf-run-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Assert.Null(Lobbies.BackendUrl(run));
                Assert.Null(Lobbies.BackendUrl(null));
                Directory.CreateDirectory(Path.Combine(run, "backend"));
                // as HellForge 0.4.0-beta writes it (a run of 2026-11-10)
                File.WriteAllText(Path.Combine(run, "backend", "ready.json"),
                    "{\"scope\":\"synthetic-local-backend\",\"urls\":[\"http://127.0.0.1:42385\"],\"mode\":\"LocalSession\",\"startupFixtures\":true,\"lifetimeSeconds\":0}");
                Assert.Equal("http://127.0.0.1:42385", Lobbies.BackendUrl(run));
                // only a loopback address is ever asked
                File.WriteAllText(Path.Combine(run, "backend", "ready.json"), "{\"urls\":[\"http://10.0.0.5:42385\"]}");
                Assert.Null(Lobbies.BackendUrl(run));
            }
            finally { try { Directory.Delete(run, true); } catch (Exception) { } }
        }

        [Fact]
        public void OnlyWebAddressesAreOpened()
        {
            Assert.Equal("https://example.com/mod", Lobbies.WebLink("https://example.com/mod"));
            Assert.NotNull(Lobbies.WebLink("http://example.com/"));
            Assert.Null(Lobbies.WebLink("file:///C:/Windows/System32/cmd.exe"));
            Assert.Null(Lobbies.WebLink("C:\\Windows\\System32\\cmd.exe"));
            Assert.Null(Lobbies.WebLink("javascript:alert(1)"));
            Assert.Null(Lobbies.WebLink("steam://run/553850"));
            Assert.Null(Lobbies.WebLink(null));
        }

        /// <summary>A backend on loopback that answers as BrowseApi does, and refuses (403
        /// HELLFORGE_NOT_FROM_THIS_PC) a request that carries a browser's headers or another Host.</summary>
        sealed class FakeBackend : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            readonly CancellationTokenSource stop = new CancellationTokenSource();
            public readonly List<string> Requests = new List<string>();
            public string Url => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;

            public FakeBackend()
            {
                listener.Start();
                Task.Run(Serve);
            }

            async Task Serve()
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await listener.AcceptTcpClientAsync(); }
                    catch (Exception) { return; }
                    using (c)
                    using (var s = c.GetStream())
                    {
                        var head = new StringBuilder();
                        var buf = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n") && s.Read(buf, 0, 1) == 1) head.Append((char)buf[0]);
                        var text = head.ToString();
                        lock (Requests) Requests.Add(text);
                        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                        var first = lines[0].Split(' ');
                        var headers = lines.Skip(1).Select(l => l.Split(new[] { ':' }, 2)).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p.Length > 1 ? p[1].Trim() : "");
                        int code; string body;
                        bool browser = headers.ContainsKey("origin") || headers.ContainsKey("referer") || headers.Keys.Any(k => k.StartsWith("sec-fetch-"));
                        bool host = headers.TryGetValue("host", out var h) && (h.StartsWith("127.0.0.1:") || h.StartsWith("localhost:") || h.StartsWith("[::1]:"));
                        if (browser || !host) { code = 403; body = "{\"errorCode\": \"HELLFORGE_NOT_FROM_THIS_PC\"}"; }
                        else if (first[0] == "GET" && first[1] == "/hellforge/v1/lobbies") { code = 200; body = Sample; }
                        else if (first[0] == "POST" && first[1].EndsWith("-000000000001/join")) { code = 409; body = "{\"errorCode\": \"HELLFORGE_LOBBY_NOT_JOINABLE\", \"message\": \"It needs mods installed by hand.\"}"; }
                        else if (first[0] == "POST" && first[1].EndsWith("/join")) { code = 202; body = "{\"version\": 1, \"state\": \"invited\"}"; }
                        else if (first[0] == "POST" && first[1] == "/hellforge/v1/lobbies/quick") { code = 404; body = "{\"errorCode\": \"HELLFORGE_NO_JOINABLE_LOBBY\"}"; }
                        else if (first[0] != "GET" && first[0] != "POST") { code = 405; body = ""; }
                        else { code = 404; body = "{\"errorCode\": \"HELLFORGE_LOBBY_UNKNOWN\"}"; }
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " X\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                        s.Write(reply, 0, reply.Length);
                        s.Write(bytes, 0, bytes.Length);
                    }
                }
            }

            public void Dispose() { stop.Cancel(); listener.Stop(); }
        }

        [Fact]
        public void RequestsReachTheBackendAsThisPcAndNeverThroughAProxy()
        {
            var before = WebRequest.DefaultWebProxy;
            // a system proxy that answers nothing (as a proxy tool that is on but not running)
            WebRequest.DefaultWebProxy = new WebProxy("http://127.0.0.1:1", false);
            try
            {
                using (var b = new FakeBackend())
                {
                    var l = Lobbies.List(b.Url);
                    Assert.Equal(2, l.Lobbies.Count);
                    var ok = Lobbies.Join(b.Url, "6f1c2d3e-0000-4000-8000-000000000002");
                    Assert.Equal(202, ok.Code);
                    var refused = Lobbies.Join(b.Url, "6f1c2d3e-0000-4000-8000-000000000001");
                    Assert.Equal(409, refused.Code);
                    Assert.Equal("It needs mods installed by hand.", refused.Message);
                    var quick = Lobbies.Quick(b.Url);
                    Assert.Equal(404, quick.Code);
                    Assert.Equal("HELLFORGE_NO_JOINABLE_LOBBY", quick.Error);
                    Assert.Equal(4, b.Requests.Count);
                    Assert.All(b.Requests, r => Assert.DoesNotContain("Origin:", r));
                }
            }
            finally { WebRequest.DefaultWebProxy = before; }
        }

        [Fact]
        public void ABackendThatIsGoneIsAReasonNotACrash()
        {
            int port;
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            Assert.Throws<HfError>(() => Lobbies.List("http://127.0.0.1:" + port));
            Assert.Equal(0, Lobbies.Quick("http://127.0.0.1:" + port).Code);
        }
    }
}
