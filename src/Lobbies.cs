using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Hd2Qd.HellForge
{
    /// <summary>HellForge's lobby browser API (external-launchers.md, "The lobby browser API"; the
    /// backend's Multiplayer\BrowseApi.cs): while a session runs with co-op on, its local backend
    /// lists every HellForge lobby the lobby directory has, with no planet or difficulty filter
    /// (the game's own Quick Play and SOS search keep only the same planet and exact difficulty),
    /// and hands the game an invitation to one. Version 1 only grows.
    ///
    /// Where: the run's backend\ready.json, "urls" (http://127.0.0.1:&lt;port&gt;). Who: programs on
    /// this PC; a request with Origin, Referer or Sec-Fetch-* headers, or another Host, gets 403.
    /// So requests go straight to loopback, never through the system proxy.</summary>
    static class Lobbies
    {
        public sealed class Mod
        {
            public string Id, Name, Version, Tier, Class, Action, Why, Url;
        }

        public sealed class Lobby
        {
            public string Id, Host, HostName, Source, Verdict, Reason, Fix;
            public bool Friend, Locked, InMission, Older, Joinable;
            public long Members, MaxMembers, Privacy, DownloadBytes;
            public long? Difficulty, PlanetId, Race;
            public List<string> Players = new List<string>();
            public List<Mod> Mods = new List<Mod>();
        }

        public sealed class Needed
        {
            public string Id, Name, Url;
            public long Lobbies;
            public List<string> Versions = new List<string>();
        }

        public sealed class Listing
        {
            public bool Available;
            public string Reason, Searched;
            public List<Lobby> Lobbies = new List<Lobby>();
            public List<Needed> Needed = new List<Needed>();
        }

        /// <summary>The backend's address for a run, from its backend\ready.json; null when there is
        /// none (no session, or one still starting).</summary>
        public static string BackendUrl(string runFolder)
        {
            if (string.IsNullOrEmpty(runFolder)) return null;
            try
            {
                var p = Path.Combine(runFolder, "backend", "ready.json");
                if (!File.Exists(p)) return null;
                var urls = Json.List(Json.Obj(File.ReadAllText(p, Encoding.UTF8)), "urls").OfType<string>();
                return urls.FirstOrDefault(u => u.StartsWith("http://127.0.0.1:") || u.StartsWith("http://localhost:") || u.StartsWith("http://[::1]:"))?.TrimEnd('/');
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static long? MaybeLong(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? Json.Long(d, key) : (long?)null;

        public static Listing Parse(Dictionary<string, object> root)
        {
            var l = new Listing
            {
                Available = Json.Bool(root, "available"), Reason = Json.Str(root, "reason"), Searched = Json.Str(root, "searchedUtc"),
            };
            foreach (var o in Json.Objs(root, "lobbies"))
            {
                var lobby = new Lobby
                {
                    Id = Json.Str(o, "lobbyId"), Host = Json.Str(o, "host"), HostName = Json.Str(o, "hostName"), Source = Json.Str(o, "source"),
                    Friend = Json.Bool(o, "friend"), Members = Json.Long(o, "members"), MaxMembers = Json.Long(o, "maxMembers", 4),
                    Locked = Json.Bool(o, "locked"), Privacy = Json.Long(o, "privacy"), Difficulty = MaybeLong(o, "difficulty"),
                    PlanetId = MaybeLong(o, "planetId"), Race = MaybeLong(o, "race"), InMission = Json.Bool(o, "inMission"), Older = Json.Bool(o, "older"),
                    Players = Json.Strs(o, "players"), Joinable = Json.Bool(o, "joinable"), Verdict = Json.Str(o, "verdict"), Reason = Json.Str(o, "reason"),
                    Fix = Json.Str(o, "fix"), DownloadBytes = Json.Long(o, "downloadBytes"),
                };
                foreach (var m in Json.Objs(o, "mods"))
                    lobby.Mods.Add(new Mod
                    {
                        Id = Json.Str(m, "id"), Name = Json.Str(m, "name"), Version = Json.Str(m, "version"), Tier = Json.Str(m, "tier"),
                        Class = Json.Str(m, "class"), Action = Json.Str(m, "action"), Why = Json.Str(m, "why"), Url = Json.Str(m, "url"),
                    });
                l.Lobbies.Add(lobby);
            }
            foreach (var n in Json.Objs(root, "needed"))
                l.Needed.Add(new Needed { Id = Json.Str(n, "id"), Name = Json.Str(n, "name"), Url = Json.Str(n, "url"), Lobbies = Json.Long(n, "lobbies"), Versions = Json.Strs(n, "versions") });
            return l;
        }

        /// <summary>An answer: the status code and the body.</summary>
        public sealed class Answer
        {
            public int Code;
            public string Body = "";
            public Dictionary<string, object> Json => HellForge.Json.Obj(Body);
            /// <summary>The API's error code (HELLFORGE_...), or null.</summary>
            public string Error => HellForge.Json.Str(Json, "errorCode");
            public string Message => HellForge.Json.Str(Json, "message");
        }

        /// <summary>Tests only: the request instead of the network.</summary>
        public static Func<string, string, Answer> Http = Send;

        /// <summary>One request to the backend: GET or POST, no body, straight to loopback.</summary>
        static Answer Send(string method, string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Proxy = null;                       // never the system proxy: the API answers this PC only
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.UserAgent = "HD2QD.HellForge";
            req.Accept = "application/json";
            if (method == "POST") req.ContentLength = 0;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return Read(resp);
            }
            catch (WebException e) when (e.Response is HttpWebResponse r)
            {
                using (r) return Read(r);
            }
        }

        static Answer Read(HttpWebResponse r)
        {
            using (var s = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return new Answer { Code = (int)r.StatusCode, Body = s.ReadToEnd() };
        }

        /// <summary>Every lobby, or HfError with the reason.</summary>
        public static Listing List(string backend)
        {
            Answer a;
            try { a = Http("GET", backend + "/hellforge/v1/lobbies"); }
            catch (WebException e) { throw new HfError(e.Message); }
            if (a.Code != 200) throw new HfError(a.Message ?? a.Error ?? ("HTTP " + a.Code));
            return Parse(a.Json);
        }

        /// <summary>An invitation to one lobby (202), or what went wrong.</summary>
        public static Answer Join(string backend, string lobbyId) =>
            Try(() => Http("POST", backend + "/hellforge/v1/lobbies/" + Uri.EscapeDataString(lobbyId) + "/join"));

        /// <summary>Quick Play: an invitation to the best joinable lobby (202), or what went wrong.</summary>
        public static Answer Quick(string backend) => Try(() => Http("POST", backend + "/hellforge/v1/lobbies/quick"));

        static Answer Try(Func<Answer> f)
        {
            try { return f(); }
            catch (WebException e) { return new Answer { Code = 0, Body = Json.Write(new Dictionary<string, object> { ["message"] = e.Message }) }; }
        }

        /// <summary>An address from a lobby's data (a stranger's mod list may carry any text): only
        /// an absolute http or https address, else null.</summary>
        public static string WebLink(string url) =>
            Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.AbsoluteUri : null;

        // ---------------------------------------------------------------- words for the player

        /// <summary>The faction a lobby's race number names, as the war's owner numbers (1 Super
        /// Earth, 2 Terminids, 3 Automatons, 4 Illuminate); null when unknown.</summary>
        public static string RaceKey(long? race)
        {
            switch (race)
            {
                case 2: return "race_terminids";
                case 3: return "race_automatons";
                case 4: return "race_illuminate";
                default: return null;
            }
        }

        public static string Size(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB"
            : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.0") + " MB"
            : Math.Max(1, bytes / 1024) + " KB";
    }
}
