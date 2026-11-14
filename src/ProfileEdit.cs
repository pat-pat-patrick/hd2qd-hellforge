using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Qd.HellForge
{
    /// <summary>A HellForge profile (profiles\&lt;name&gt;\profile.json) changed between sessions, as
    /// HellForge itself changes it: weapon levels as its launcher's WeaponLevels.Save (a progress entry
    /// per package, the items of every level up to it granted, nothing taken away), warbond items as its
    /// backend's Purchases.WarbondItem (the item id recorded, the item's contents granted, the warbond's
    /// medals-spent count raised so its pages open; a page's Super Credits are left out, as HellForge never
    /// stores them), amounts within the catalogue's cap. Premium content stays HellForge's to serve: its
    /// OwnershipFilter serves a paid item only to a player who owns it on the live game, whatever the
    /// profile holds, and the editor offers only the warbonds the profile owns. HellForge validates the
    /// file when a session loads it; an invalid one is reported, never replaced.</summary>
    sealed class ProfileEdit
    {
        public readonly string Folder;
        public readonly Dictionary<string, object> Doc;

        public ProfileEdit(string folder)
        {
            Folder = folder;
            Doc = Json.Obj(System.IO.File.ReadAllText(File(folder), Encoding.UTF8)) ?? throw new HfError("profile.json cannot be read: " + File(folder));
        }

        public static string File(string folder) => Path.Combine(folder, "profile.json");

        Dictionary<string, object> Section(string key)
        {
            if (!(Doc.TryGetValue(key, out var v) && v is Dictionary<string, object> d)) Doc[key] = d = new Dictionary<string, object>();
            return d;
        }

        Dictionary<string, object> Progression => Section("progression");

        /// <summary>An array of the document as a list the edits can change (stored back in place).</summary>
        static List<object> Arr(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out var v) && v is List<object> l) return l;
            var list = v is IEnumerable e && !(v is string) ? e.Cast<object>().ToList() : new List<object>();
            d[key] = list;
            return list;
        }

        static long Num(object v) => v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);

        List<object> Inventory => Arr(Section("inventory"), "items");

        // ---------------------------------------------------------------- reading

        public long Experience => Json.Long(Progression, "experience");

        /// <summary>The level an amount of experience is (the highest whose required experience it has).</summary>
        public static int LevelOf(ProfileData.Progression p, long experience) =>
            p.Levels.Where(kv => kv.Value <= experience).Select(kv => kv.Key).DefaultIfEmpty(1).Max();

        public long Held(uint mixId) =>
            Inventory.OfType<Dictionary<string, object>>().Where(i => (uint)Num(i.TryGetValue("mixId", out var m) ? m : null) == mixId)
                     .Sum(i => Num(i.TryGetValue("amount", out var a) ? a : null));

        public HashSet<uint> HeldIds() =>
            new HashSet<uint>(Inventory.OfType<Dictionary<string, object>>().Where(i => Num(i.TryGetValue("amount", out var a) ? a : null) > 0)
                                       .Select(i => (uint)Num(i.TryGetValue("mixId", out var m) ? m : null)));

        public int WeaponLevel(uint package) =>
            Arr(Progression, "packageProgress").OfType<Dictionary<string, object>>()
                .Where(e => (uint)Num(e.TryGetValue("id32", out var id) ? id : null) == package)
                .Select(e => (int)Num(e.TryGetValue("level", out var l) ? l : null)).FirstOrDefault();

        public HashSet<long> Bought() => new HashSet<long>(Arr(Progression, "purchasedSeasonPassItems").Select(Num));

        /// <summary>Whether the profile owns a warbond: the free one, or one whose token it holds.</summary>
        public bool Owns(ProfileData.Warbond w) => w.Id == ProfileData.FreeWarbond || w.Tokens.Any(t => Held(t) > 0);

        /// <summary>How many of a warbond's pages are open (its medals spent at least each page's requirement).</summary>
        public int PagesOpen(ProfileData.Warbond w)
        {
            long spent = Held(w.MedalsSpent);
            return w.Pages.Count(p => p.Required <= spent);
        }

        // ---------------------------------------------------------------- changing

        public void SetLevel(ProfileData.Progression p, int level)
        {
            if (!p.Levels.TryGetValue(level, out var xp)) throw new HfError("no level " + level);
            Progression["experience"] = xp;
        }

        /// <summary>An item's amount set, within 0 and the catalogue's cap (when it has one).</summary>
        public void SetAmount(uint mixId, long amount, long cap = 0)
        {
            amount = Math.Max(0, cap > 0 ? Math.Min(amount, cap) : amount);
            var inv = Inventory;
            var entry = inv.OfType<Dictionary<string, object>>().FirstOrDefault(i => (uint)Num(i.TryGetValue("mixId", out var m) ? m : null) == mixId);
            if (entry != null) entry["amount"] = amount;
            else if (amount > 0) inv.Add(new Dictionary<string, object> { ["mixId"] = mixId, ["amount"] = amount, ["isDiscovered"] = true });
        }

        /// <summary>An item added once (as HellForge grants it: amount 1, discovered); false when it was held.</summary>
        public bool Grant(uint mixId)
        {
            if (mixId == 0 || mixId == ProfileData.SuperCreditsWallet || HeldIds().Contains(mixId)) return false;
            Inventory.Add(new Dictionary<string, object> { ["mixId"] = mixId, ["amount"] = 1L, ["isDiscovered"] = true });
            return true;
        }

        /// <summary>A weapon's mastery level (0 removes its progress entry), and every item of its levels up to it.</summary>
        public void SetWeaponLevel(ProfileData.Package w, int level)
        {
            level = Math.Max(0, Math.Min(level, w.MaxLevel));
            var progress = Arr(Progression, "packageProgress");
            var entry = progress.OfType<Dictionary<string, object>>().FirstOrDefault(e => (uint)Num(e.TryGetValue("id32", out var id) ? id : null) == w.Id);
            if (level == 0) { if (entry != null) progress.Remove(entry); return; }
            if (entry == null)
                progress.Add(new Dictionary<string, object> { ["id32"] = w.Id, ["level"] = level, ["progress"] = 0, ["sortOrder"] = 0, ["claimedLevels"] = new List<object>() });
            else if (Num(entry.TryGetValue("level", out var l) ? l : null) != level) { entry["level"] = level; entry["progress"] = 0; }
            foreach (var kv in w.Grants.Where(g => g.Key <= level))
                foreach (var id in kv.Value) Grant(id);
        }

        /// <summary>A warbond's pages up to `pages` bought: each item not bought yet recorded and its contents
        /// granted, the medals spent raised to open them. Only a warbond the profile owns. Returns the items bought.</summary>
        public int UnlockWarbond(ProfileData.Progression p, ProfileData.Warbond w, int pages)
        {
            if (!Owns(w)) throw new HfError("this warbond is not owned in this profile");
            var bought = Arr(Progression, "purchasedSeasonPassItems");
            var have = new HashSet<long>(bought.Select(Num));
            long spentNeeded = 0;
            int n = 0;
            foreach (var (_, items) in w.Pages.Take(Math.Max(0, pages)))
                foreach (var it in items)
                {
                    spentNeeded += Math.Max(0, it.Cost);
                    if (it.Repeatable || have.Contains(it.Id)) continue;
                    var contents = p.ItemPackages.TryGetValue(it.MixId, out var pack) ? pack.Select(x => x.MixId) : new[] { it.MixId };
                    foreach (var c in contents) Grant(c);
                    bought.Add(it.Id);
                    have.Add(it.Id);
                    n++;
                }
            // the pages open by the medals spent in the warbond (Purchases.WarbondItem raises it by each price)
            var lastRequired = w.Pages.Take(Math.Max(0, pages)).Select(pg => pg.Required).DefaultIfEmpty(0).Max();
            if (Held(w.MedalsSpent) < Math.Max(spentNeeded, lastRequired)) SetAmount(w.MedalsSpent, Math.Max(spentNeeded, lastRequired));
            return n;
        }

        /// <summary>Every item of a category the profile lacks granted (consumables left out: they are
        /// amounts, not things owned). Returns how many.</summary>
        public int UnlockCategory(IEnumerable<ProfileData.Item> catalogue, int category) =>
            catalogue.Where(i => i.Category == category && !i.Consumable).Count(i => Grant(i.MixId));

        // ---------------------------------------------------------------- saving

        /// <summary>The profile as it was, into its backups folder as HellForge names them
        /// (ProfileBackups: "&lt;UTC yyyyMMdd-HHmmss-fff&gt;-&lt;reason&gt;.json", newest ten kept), so HellForge's
        /// own Account sync page lists it and restores it.</summary>
        public static string Backup(string folder, DateTime nowUtc)
        {
            var profile = File(folder);
            if (!System.IO.File.Exists(profile)) return null;
            var dir = Directory.CreateDirectory(Path.Combine(folder, "backups")).FullName;
            var path = Path.Combine(dir, nowUtc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-quickdeploy.json");
            System.IO.File.Copy(profile, path, true);
            foreach (var old in Backups(folder).Skip(10)) System.IO.File.Delete(old.Path);
            return path;
        }

        /// <summary>The profile's backups, newest first (files named otherwise are left out, as HellForge does).</summary>
        public static List<(string Path, DateTime SavedUtc, string Reason)> Backups(string folder)
        {
            var list = new List<(string, DateTime, string)>();
            var dir = Path.Combine(folder, "backups");
            if (!Directory.Exists(dir)) return list;
            foreach (var p in Directory.EnumerateFiles(dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(p);
                const string stamp = "yyyyMMdd-HHmmss-fff";
                if (name.Length <= stamp.Length + 1 || name[stamp.Length] != '-' ||
                    !DateTime.TryParseExact(name.Substring(0, stamp.Length), stamp, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var saved)) continue;
                list.Add((p, saved, name.Substring(stamp.Length + 1)));
            }
            return list.OrderByDescending(b => b.Item2).ThenByDescending(b => b.Item1, StringComparer.Ordinal).ToList();
        }

        /// <summary>A backup put back: the profile as it is backed up first, then replaced.</summary>
        public static void Restore(string folder, string backup, DateTime nowUtc)
        {
            if (Json.Obj(System.IO.File.ReadAllText(backup, Encoding.UTF8)) == null) throw new HfError("the backup is not a profile: " + backup);
            Backup(folder, nowUtc);
            Write(File(folder), System.IO.File.ReadAllText(backup, Encoding.UTF8));
        }

        /// <summary>The profile written whole (indented, as HellForge writes it), to a temporary file then renamed.</summary>
        public void Save() => Write(File(Folder), Indent(Json.Write(Doc)) + "\n");

        static void Write(string path, string text)
        {
            var tmp = path + ".hd2qd-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            System.IO.File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
            else System.IO.File.Move(tmp, path);
        }

        /// <summary>Compact JSON indented two spaces (strings kept as they are).</summary>
        internal static string Indent(string json)
        {
            var sb = new StringBuilder(json.Length * 2);
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\') { sb.Append(json[++i]); continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{': case '[':
                        // an empty one stays on its line
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(c).Append(json[++i]); break; }
                        sb.Append(c).Append('\n').Append(' ', ++depth * 2); break;
                    case '}': case ']': sb.Append('\n').Append(' ', --depth * 2).Append(c); break;
                    case ',': sb.Append(c).Append('\n').Append(' ', depth * 2); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
