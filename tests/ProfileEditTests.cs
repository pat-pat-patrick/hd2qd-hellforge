using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hd2Qd.HellForge;
using Xunit;

namespace HellForge.Tests
{
    /// <summary>The profile editor's core (the user, 2026-11-15): HellForge's own rules for a weapon
    /// level (WeaponLevels.Save) and a warbond item (Purchases.WarbondItem), the catalogue read out of
    /// the backend's bytes, HellForge's backup names, and a profile written back unchanged otherwise.</summary>
    public class ProfileEditTests : IDisposable
    {
        readonly string Folder = Path.Combine(Path.GetTempPath(), "hd2qd-hf-profile-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        const uint Weapon = 0xAA035309, Attachment = 0x11111111, Reward = 0x22222222;
        const uint Medals = 1276729503, Spent = 659494344, Token = 0x33333333, Pack = 0x44444444, InPack = 0x55555555, Cape = 0x66666666;
        const uint Paid = 610512218;

        public ProfileEditTests()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(ProfileEdit.File(Folder), "{\"schemaVersion\": 1, \"progression\": {\"experience\": 100, \"onboardingState\": 128, " +
                "\"packageProgress\": [], \"purchasedSeasonPassItems\": [], \"shipName\": [29, 34], \"somethingNew\": {\"kept\": [1, 2]}}, " +
                "\"inventory\": {\"items\": [{\"mixId\": " + Medals + ", \"amount\": 40, \"isDiscovered\": true}]}, \"customization\": {\"items\": []}}");
        }

        public void Dispose() { try { Directory.Delete(Folder, true); } catch (IOException) { } }

        static ProfileData.Progression Progression() => ProfileData.ParseProgression(Json.Obj(
            "{\"levelSpec\": [{\"level\": 1, \"requiredExperience\": 1}, {\"level\": 2, \"requiredExperience\": 500}, {\"level\": 3, \"requiredExperience\": 1500}]," +
            " \"itemPackages\": [{\"itemMixId32\": " + Pack + ", \"items\": [{\"mixId\": " + InPack + ", \"amount\": 1}]}]," +
            " \"progressionPackages\": [{\"id32\": 7, \"type\": 2, \"requiredItems\": [" + Weapon + "], \"levels\": [" +
            "   {\"level\": 1, \"rewards\": [], \"unlockableItems\": [{\"mixId\": " + Attachment + "}]}," +
            "   {\"level\": 2, \"rewards\": [{\"type\": 1, \"id32\": " + Reward + ", \"amount\": 1}], \"unlockableItems\": []}]}," +
            "  {\"id32\": 8, \"type\": 1, \"requiredItems\": [1], \"levels\": []}]," +
            " \"seasonPass\": [{\"id32\": " + ProfileData.FreeWarbond + "}, {\"id32\": " + Paid + "}]," +
            " \"seasonPassDetails\": {" +
            "  \"" + ProfileData.FreeWarbond + "\": {\"id32\": " + ProfileData.FreeWarbond + ", \"medalsConsumableMixId\": " + Medals + ", \"medalsSpentMixId\": " + Spent + ", \"premiumTokenMixIds\": []," +
            "   \"seasonPassPages\": [{\"totalMedalsRequired\": 0, \"seasonPassItems\": [{\"seasonPassItemId\": 1, \"itemMixId\": " + Pack + ", \"medalCost\": 5, \"isRepeatable\": false}," +
            "                                                                         {\"seasonPassItemId\": 2, \"itemMixId\": " + ProfileData.SuperCreditsWallet + ", \"medalCost\": 3, \"isRepeatable\": false}]}," +
            "                        {\"totalMedalsRequired\": 20, \"seasonPassItems\": [{\"seasonPassItemId\": 3, \"itemMixId\": " + Cape + ", \"medalCost\": 9, \"isRepeatable\": false}]}]}," +
            "  \"" + Paid + "\": {\"id32\": " + Paid + ", \"medalsConsumableMixId\": " + Medals + ", \"medalsSpentMixId\": 99, \"premiumTokenMixIds\": [" + Token + "]," +
            "   \"seasonPassPages\": [{\"totalMedalsRequired\": 0, \"seasonPassItems\": [{\"seasonPassItemId\": 50, \"itemMixId\": 5, \"medalCost\": 1, \"isRepeatable\": false}]}]}}}"));

        [Fact]
        public void ProgressionDataIsReadAsHellForgeLaysItOut()
        {
            var p = Progression();
            Assert.Equal(3, p.Levels.Count);
            Assert.Equal(Medals, p.MedalsConsumable);
            var w = Assert.Single(p.Weapons);                                     // the type 1 package is no weapon's mastery
            Assert.Equal(Weapon, w.Weapon);
            Assert.Equal(2, w.MaxLevel);
            Assert.Equal(new[] { Attachment }, w.Grants[1]);
            Assert.Equal(2, p.Warbonds.Count);
            Assert.Equal(2, p.Warbonds[0].Pages.Count);
            Assert.Equal(20, p.Warbonds[0].Pages[1].Required);
            Assert.Equal(InPack, p.ItemPackages[Pack].Single().MixId);
        }

        [Fact]
        public void LevelsAmountsAndWeaponLevelsAreSetAsHellForgeSetsThem()
        {
            var p = Progression();
            var e = new ProfileEdit(Folder);
            Assert.Equal(1, ProfileEdit.LevelOf(p, e.Experience));
            e.SetLevel(p, 3);
            Assert.Equal(1500, e.Experience);
            Assert.Throws<HfError>(() => e.SetLevel(p, 4));                       // must die: no such level
            e.SetAmount(Medals, 900, 250);                                          // within the cap
            Assert.Equal(250, e.Held(Medals));
            e.SetAmount(ProfileData.Requisition, 1234);
            Assert.Equal(1234, e.Held(ProfileData.Requisition));
            var w = p.Weapons.Single();
            e.SetWeaponLevel(w, 2);
            Assert.Equal(2, e.WeaponLevel(w.Id));
            Assert.Contains(Attachment, e.HeldIds());
            Assert.Contains(Reward, e.HeldIds());
            e.SetWeaponLevel(w, 0);                                                 // the entry goes, the items stay
            Assert.Equal(0, e.WeaponLevel(w.Id));
            Assert.Contains(Reward, e.HeldIds());
            e.Save();
            var again = new ProfileEdit(Folder);
            Assert.Equal(1500, again.Experience);
            Assert.Equal(250, again.Held(Medals));
            // must live: what the editor does not know is written back as it was
            var raw = Json.Obj(File.ReadAllText(ProfileEdit.File(Folder)));
            Assert.Equal(new object[] { 1, 2 }, Json.List(Json.Obj(Json.Obj(raw, "progression"), "somethingNew"), "kept"));
            Assert.Equal(new object[] { 29, 34 }, Json.List(Json.Obj(raw, "progression"), "shipName"));
            Assert.Equal(128L, Json.Long(Json.Obj(raw, "progression"), "onboardingState"));
            Assert.Contains("\n  \"progression\": {", File.ReadAllText(ProfileEdit.File(Folder)));   // indented, as HellForge writes it
        }

        [Fact]
        public void AWarbondsPagesAreBoughtAsTheBackendBuysThem()
        {
            var p = Progression();
            var e = new ProfileEdit(Folder);
            var free = p.Warbonds[0];
            Assert.True(e.Owns(free));
            Assert.Equal(1, e.PagesOpen(free));
            Assert.Equal(2, e.UnlockWarbond(p, free, 1));                           // the first page: two items
            Assert.Contains(InPack, e.HeldIds());                                  // a package's contents, not the package
            Assert.DoesNotContain(Pack, e.HeldIds());
            Assert.DoesNotContain(ProfileData.SuperCreditsWallet, e.HeldIds());    // never stored
            Assert.Equal(new long[] { 1, 2 }, e.Bought().OrderBy(x => x));
            Assert.Equal(40, e.Held(Medals));                                      // no medals spent
            Assert.Equal(1, e.PagesOpen(free));                                     // 8 spent: page 2 needs 20
            Assert.Equal(1, e.UnlockWarbond(p, free, 2));
            Assert.Equal(2, e.PagesOpen(free));
            Assert.Contains(Cape, e.HeldIds());
            Assert.Equal(0, e.UnlockWarbond(p, free, 2));                          // nothing twice
            // must die: a premium warbond the profile does not own
            var paid = p.Warbonds[1];
            Assert.False(e.Owns(paid));
            Assert.Throws<HfError>(() => e.UnlockWarbond(p, paid, 1));
            Assert.DoesNotContain(50L, e.Bought());
            // must live: once its token is held
            e.SetAmount(Token, 1);
            Assert.Equal(1, e.UnlockWarbond(p, paid, 1));
        }

        [Fact]
        public void ACategoryIsUnlockedWithoutItsConsumables()
        {
            var cat = new List<ProfileData.Item>
            {
                new ProfileData.Item { MixId = 1, Category = 9 }, new ProfileData.Item { MixId = 2, Category = 9 },
                new ProfileData.Item { MixId = 3, Category = 9, Consumable = true }, new ProfileData.Item { MixId = 4, Category = 7 },
            };
            var e = new ProfileEdit(Folder);
            e.Grant(1);
            Assert.Equal(1, e.UnlockCategory(cat, 9));
            Assert.Contains(2u, e.HeldIds());
            Assert.DoesNotContain(3u, e.HeldIds());
            Assert.DoesNotContain(4u, e.HeldIds());
        }

        [Fact]
        public void BackupsAreNamedAsHellForgeNamesThemAndARestoreBacksUpFirst()
        {
            var t = new DateTime(2026, 11, 15, 1, 2, 3, 456, DateTimeKind.Utc);
            var path = ProfileEdit.Backup(Folder, t);
            Assert.Equal("20261115-010203-456-quickdeploy.json", Path.GetFileName(path));
            File.WriteAllText(Path.Combine(Folder, "backups", "notes.json"), "{}");    // not a backup: left out
            var b = Assert.Single(ProfileEdit.Backups(Folder));
            Assert.Equal("quickdeploy", b.Reason);
            Assert.Equal(t, b.SavedUtc);
            var e = new ProfileEdit(Folder);
            e.SetAmount(Medals, 7);
            e.Save();
            ProfileEdit.Restore(Folder, path, t.AddMinutes(1));
            Assert.Equal(40, new ProfileEdit(Folder).Held(Medals));
            Assert.Equal(2, ProfileEdit.Backups(Folder).Count);                   // the one with 7 kept first
            for (int i = 0; i < 12; i++) ProfileEdit.Backup(Folder, t.AddHours(i + 1));
            Assert.Equal(10, ProfileEdit.Backups(Folder).Count);                  // HellForge keeps ten
        }

        [Fact]
        public void TheCatalogueIsFoundInTheBackendsBytes()
        {
            var json = "{\"scope\":\"Catalogue rebuilt\",\"items\":[{\"itemId\":5,\"mixId\":5,\"isConsumable\":false,\"progressionCategory\":9,\"tags\":[],\"requiredItems\":[]," +
                       "\"buyPrice\":[],\"sellPrice\":[],\"note\":\"a } in a string\"},{\"itemId\":6,\"parentId\":5,\"mixId\":7,\"isConsumable\":true,\"progressionCategory\":2,\"maxCap\":10}]}";
            var bytes = Encoding.UTF8.GetBytes("MZ\0junk{\"scope\":\"Catalogue\" broken\0" + json + "\0more junk");
            var doc = ProfileData.ReadCatalogue(bytes);
            Assert.NotNull(doc);
            Assert.Equal(2, Json.Objs(doc, "items").Count);
            Assert.Null(ProfileData.ReadCatalogue(Encoding.UTF8.GetBytes("MZ nothing here")));   // must die: none
        }

        [Fact]
        public void AnEnglishNameBecomesTheGamesOwnText()
        {
            var t = new ProfileData.GameText();
            t.English["Liberator"] = 1; t.Local[1] = "解放者";
            t.English["Cutting Edge"] = 2; t.Local[2] = "遥遥领先";
            Assert.Equal("AR-23 解放者", t.Official("AR-23 Liberator"));
            Assert.Equal("遥遥领先", t.Official("Cutting Edge"));
            Assert.Null(t.Official("Something the game does not say"));
        }
    }
}
