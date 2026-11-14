using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Qd.HellForge
{
    /// <summary>What the profile editor knows about the items a profile holds, read at run time from
    /// the player's own HellForge (the user, 2026-11-15: a profile page that edits the profile, by
    /// category, names where they can be had). Nothing of HellForge is shipped with the plugin
    /// (HellForge is GPL-3.0, the plugin MIT): it is read where it is installed, as its icon is.
    /// - The item catalogue (each item's progressionCategory, parent, consumable, cap): HellForge's
    ///   local backend serves it as /Progression/items and holds it as JSON text in
    ///   app\backend\HellForge.LocalBackend.exe (src/HellForge.LocalBackend/item-catalog.json); read
    ///   from there, kept in home\hellforge\catalogue.json while that file is unchanged.
    /// - app\progression-data.json: the level table, each weapon's mastery package, the warbonds.
    /// - Names: the item ids are in no file of the game (research/item_names, 2026-11-15), so a name
    ///   comes from the newest session's arsenal.tsv (the game's weapon registry, in the game's
    ///   language), else from the community's English name (Data\community-names.json,
    ///   helldivers-2/json, MIT) turned into the game's official text in the window's language
    ///   through the game's own strings (the same string id in each language).</summary>
    static class ProfileData
    {
        public sealed class Item
        {
            public uint ItemId, MixId, Parent;
            public int Category;
            public bool Consumable;
            public long Cap;
        }

        public sealed class Package
        {
            public uint Id, Weapon;
            public int MaxLevel;
            /// <summary>Per level (1..MaxLevel): the items it gives (rewards of type 1, and unlockable items).</summary>
            public Dictionary<int, List<uint>> Grants = new Dictionary<int, List<uint>>();
        }

        public sealed class WarbondItem
        {
            public long Id;
            public uint MixId;
            public long Cost;
            public bool Repeatable;
        }

        public sealed class Warbond
        {
            public uint Id, MedalsSpent;
            public List<uint> Tokens = new List<uint>();
            /// <summary>Its pages in order: the medals spent that open each, and its items.</summary>
            public List<(long Required, List<WarbondItem> Items)> Pages = new List<(long, List<WarbondItem>)>();
            public IEnumerable<WarbondItem> Items => Pages.SelectMany(p => p.Items);
        }

        public sealed class Progression
        {
            /// <summary>Level -> the experience it starts at.</summary>
            public SortedDictionary<int, long> Levels = new SortedDictionary<int, long>();
            public List<Package> Weapons = new List<Package>();
            public List<Warbond> Warbonds = new List<Warbond>();
            /// <summary>An item that is a package of others (a warbond's item bundle): what it gives.</summary>
            public Dictionary<uint, List<(uint MixId, long Amount)>> ItemPackages = new Dictionary<uint, List<(uint, long)>>();
            public uint MedalsConsumable;
        }

        /// <summary>The warbond everyone owns (HellForge.Core PremiumContent.FreeWarbond).</summary>
        public const uint FreeWarbond = 1929468580;
        /// <summary>The wallet a page's Super Credits go into, which HellForge never stores (Purchases.cs).</summary>
        public const uint SuperCreditsWallet = 0xCBFFC645;
        /// <summary>Requisition slips (the attachments' price in the catalogue).</summary>
        public const uint Requisition = 3608481516;
        /// <summary>HellForge's own currency, which buys an owned premium warbond's token (PaidContentSet.ForgeCreditsWallet).</summary>
        public const uint ForgeCredits = 0x6C3C83C1;

        // ---------------------------------------------------------------- the catalogue

        public static string Backend(string install) => Path.Combine(install, "app", "backend", "HellForge.LocalBackend.exe");

        /// <summary>HellForge's item catalogue, from its backend (kept in home while that file is unchanged).</summary>
        public static List<Item> Catalogue(string home, string install)
        {
            var exe = new FileInfo(Backend(install));
            if (!exe.Exists) throw new HfError("HellForge's backend is missing: " + exe.FullName);
            var stamp = exe.Length + ":" + exe.LastWriteTimeUtc.Ticks;
            var cache = Path.Combine(home, "hellforge", "catalogue.json");
            Dictionary<string, object> doc = null;
            try
            {
                if (File.Exists(cache))
                {
                    var kept = Json.Obj(File.ReadAllText(cache, Encoding.UTF8));
                    if (Json.Str(kept, "stamp") == stamp) doc = Json.Obj(kept, "catalogue");
                }
            }
            catch (IOException) { }
            if (doc == null)
            {
                doc = ReadCatalogue(File.ReadAllBytes(exe.FullName)) ?? throw new HfError("HellForge's item catalogue was not found in " + exe.Name);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache));
                    File.WriteAllText(cache, Json.Write(new Dictionary<string, object> { ["stamp"] = stamp, ["catalogue"] = doc }), new UTF8Encoding(false));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return Json.Objs(doc, "items").Select(i => new Item
            {
                ItemId = U32(i, "itemId"), MixId = U32(i, "mixId"), Parent = U32(i, "parentId"),
                Category = (int)Json.Long(i, "progressionCategory", -1), Consumable = Json.Bool(i, "isConsumable"), Cap = Json.Long(i, "maxCap"),
            }).Where(i => i.MixId != 0).ToList();
        }

        /// <summary>The catalogue's JSON text in the backend's bytes (it starts {"scope":"Catalogue), parsed; null when absent.</summary>
        internal static Dictionary<string, object> ReadCatalogue(byte[] exe)
        {
            var key = Encoding.ASCII.GetBytes("{\"scope\":\"Catalogue");
            for (int at = IndexOf(exe, key, 0); at >= 0; at = IndexOf(exe, key, at + 1))
            {
                // the object ends where its braces close (strings in it never hold a brace unescaped in a way that matters here,
                // but they are skipped anyway)
                int depth = 0, end = -1;
                bool inString = false;
                for (int i = at; i < exe.Length; i++)
                {
                    byte c = exe[i];
                    if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}' && --depth == 0) { end = i; break; }
                }
                if (end < 0) continue;
                var doc = Json.Obj(Encoding.UTF8.GetString(exe, at, end - at + 1));
                if (doc != null && Json.Objs(doc, "items").Count > 0) return doc;
            }
            return null;
        }

        static int IndexOf(byte[] hay, byte[] needle, int from)
        {
            for (int i = Array.IndexOf(hay, needle[0], from); i >= 0 && i <= hay.Length - needle.Length; i = Array.IndexOf(hay, needle[0], i + 1))
            {
                int k = 1;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }

        internal static uint U32(Dictionary<string, object> d, string key) => (uint)Json.Long(d, key);

        // ---------------------------------------------------------------- progression-data.json

        public static string ProgressionFile(string install) => Path.Combine(install, "app", "progression-data.json");

        public static Progression ReadProgression(string install) =>
            ParseProgression(Json.Obj(File.ReadAllText(ProgressionFile(install), Encoding.UTF8)) ?? throw new HfError("progression-data.json cannot be read"));

        internal static Progression ParseProgression(Dictionary<string, object> d)
        {
            var p = new Progression();
            foreach (var l in Json.Objs(d, "levelSpec"))
                p.Levels[(int)Json.Long(l, "level")] = Json.Long(l, "requiredExperience");
            foreach (var pk in Json.Objs(d, "progressionPackages"))
            {
                var required = Json.List(pk, "requiredItems");
                if (Json.Long(pk, "type") != 2 || required.Count == 0) continue;     // type 2: a weapon's mastery (HellForge's WeaponLevels)
                var w = new Package { Id = U32(pk, "id32"), Weapon = (uint)Convert.ToInt64(required[0], CultureInfo.InvariantCulture) };
                foreach (var step in Json.Objs(pk, "levels"))
                {
                    int level = (int)Json.Long(step, "level");
                    w.MaxLevel = Math.Max(w.MaxLevel, level);
                    var grants = new List<uint>();
                    foreach (var r in Json.Objs(step, "rewards")) if (Json.Long(r, "type") == 1) grants.Add(U32(r, "id32"));
                    foreach (var u in Json.Objs(step, "unlockableItems")) grants.Add(U32(u, "mixId"));
                    w.Grants[level] = grants;
                }
                p.Weapons.Add(w);
            }
            foreach (var ip in Json.Objs(d, "itemPackages"))
                p.ItemPackages[U32(ip, "itemMixId32")] = Json.Objs(ip, "items").Select(i => (U32(i, "mixId"), Json.Long(i, "amount", 1))).ToList();
            var details = Json.Obj(d, "seasonPassDetails") ?? new Dictionary<string, object>();
            foreach (var s in Json.Objs(d, "seasonPass"))
            {
                var id = U32(s, "id32");
                var full = Json.Obj(details, id.ToString(CultureInfo.InvariantCulture)) ?? s;
                var w = new Warbond
                {
                    Id = id, MedalsSpent = U32(full, "medalsSpentMixId"),
                    Tokens = Json.List(full, "premiumTokenMixIds").Select(t => (uint)Convert.ToInt64(t, CultureInfo.InvariantCulture)).ToList(),
                };
                if (p.MedalsConsumable == 0) p.MedalsConsumable = U32(full, "medalsConsumableMixId");
                foreach (var page in Json.Objs(full, "seasonPassPages"))
                    w.Pages.Add((Json.Long(page, "totalMedalsRequired"), Json.Objs(page, "seasonPassItems").Select(i => new WarbondItem
                    {
                        Id = Json.Long(i, "seasonPassItemId"), MixId = U32(i, "itemMixId"), Cost = Json.Long(i, "medalCost"), Repeatable = Json.Bool(i, "isRepeatable"),
                    }).ToList()));
                p.Warbonds.Add(w);
            }
            return p;
        }

        // ---------------------------------------------------------------- names

        /// <summary>The community's English names (item id -> name) and warbonds (name, item ids), as shipped.</summary>
        public static (Dictionary<uint, string> Items, List<(string Name, HashSet<uint> Items)> Warbonds) Community()
        {
            using (var s = typeof(ProfileData).Assembly.GetManifestResourceStream("Data.community-names.json"))
            using (var r = new StreamReader(s, Encoding.UTF8))
            {
                var d = Json.Obj(r.ReadToEnd());
                var items = new Dictionary<uint, string>();
                foreach (var kv in Json.Obj(d, "items") ?? new Dictionary<string, object>())
                    if (uint.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && kv.Value is string n) items[id] = n;
                var warbonds = Json.Objs(d, "warbonds").Select(w => (Json.Str(w, "name"),
                    new HashSet<uint>(Json.List(w, "items").Select(x => (uint)Convert.ToInt64(x, CultureInfo.InvariantCulture))))).ToList();
                return (items, warbonds);
            }
        }

        /// <summary>The game's strings in English and in the window's language (zh-CN: Simplified Chinese,
        /// zh-TW / zh-HK: Traditional; else English), each as text -> string id and id -> text.</summary>
        public sealed class GameText
        {
            public Dictionary<string, uint> English = new Dictionary<string, uint>(StringComparer.Ordinal);
            public Dictionary<uint, string> Local = new Dictionary<uint, string>();

            /// <summary>The official text in the window's language for an English name: the whole name, or
            /// the part after a model code ("AR-23 Liberator" -> "AR-23 " + the game's "Liberator"); null when
            /// the game has none.</summary>
            public string Official(string english)
            {
                if (string.IsNullOrEmpty(english)) return null;
                if (English.TryGetValue(english.Trim(), out var id) && Local.TryGetValue(id, out var t)) return t;
                var m = System.Text.RegularExpressions.Regex.Match(english, @"^([A-Za-z0-9]+(?:[-/][A-Za-z0-9.]+)+)\s+(.+)$");
                if (m.Success && English.TryGetValue(m.Groups[2].Value.Trim(), out id) && Local.TryGetValue(id, out t)) return m.Groups[1].Value + " " + t;
                return null;
            }
        }

        public const ulong StringsType = 0x0D972BAB10B40FD3;           // murmur64("strings")
        const string StringsPackage = "9ba626afa44a3aa3";
        const uint ZhHans = 0x5942CCF7, ZhHant = 0x9EBA952A;

        /// <summary>The game's strings of HellForge's game copy (its data folder), read once per window language.</summary>
        public static GameText ReadText(string gameData, string lang)
        {
            var tables = new List<Strings.Table>();
            using (var game = new Game(gameData))
            {
                var pre = game.Read(StringsPackage, 0, 12);
                if (pre.Length < 12) throw new HfError("the game's text package is missing");
                uint tc = Game.U32(pre, 4), fc = Game.U32(pre, 8);
                var head = game.Read(StringsPackage, 0, (int)(0x48 + tc * 32 + fc * 80));
                int b = (int)(0x48 + tc * 32);
                for (int i = 0; i < fc; i++)
                {
                    int r = b + i * 80;
                    if (Game.U64(head, r + 8) != StringsType) continue;
                    var body = game.Read(StringsPackage, Game.U64(head, r + 16), (int)Game.U32(head, r + 56));
                    try { tables.Add(Strings.Parse(body)); }
                    catch (InvalidDataException) { }
                }
            }
            var byLang = tables.GroupBy(t => t.Language).ToDictionary(g => g.Key, g => g.SelectMany(t => t.Ids.Select((id, i) => (id, t.Texts[i]))).ToList());
            uint english = byLang.OrderByDescending(kv => kv.Value.Count(x => Encoding.UTF8.GetString(x.Item2) == "Machine Gun")).Select(kv => kv.Key).FirstOrDefault();
            var l = (lang ?? "").ToLowerInvariant();
            uint local = l.StartsWith("zh") ? (l.Contains("tw") || l.Contains("hk") || l.Contains("hant") ? ZhHant : ZhHans) : english;
            var text = new GameText();
            if (byLang.TryGetValue(english, out var en))
                foreach (var (id, t) in en)
                {
                    var s = Encoding.UTF8.GetString(t).Trim();
                    if (s.Length > 0 && !text.English.ContainsKey(s)) text.English[s] = id;
                }
            if (byLang.TryGetValue(local, out var lo))
                foreach (var (id, t) in lo) text.Local[id] = Encoding.UTF8.GetString(t).Trim();
            return text;
        }

        /// <summary>The newest session's arsenal.tsv: weapon item id -> "model name" in the game's language.</summary>
        public static Dictionary<uint, string> Arsenal(string install)
        {
            var names = new Dictionary<uint, string>();
            try
            {
                var sessions = Path.Combine(install, "logs", "sessions");
                if (!Directory.Exists(sessions)) return names;
                var newest = Directory.EnumerateDirectories(sessions).Select(d => new FileInfo(Path.Combine(d, "game", "arsenal.tsv")))
                    .Where(f => f.Exists).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest == null) return names;
                foreach (var line in File.ReadLines(newest.FullName, Encoding.UTF8).Skip(1))
                {
                    var f = line.Split('\t');
                    if (f.Length >= 5 && uint.TryParse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                    {
                        var n = (f[3] + " " + f[4]).Trim();
                        if (n.Length > 0) names[id] = n;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            return names;
        }
    }
}
