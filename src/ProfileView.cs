using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Hd2Qd.HellForge
{
    public static partial class Plugin
    {
        /// <summary>The profile tab (the user, 2026-11-15: the profile, and a way to edit it "by category"):
        /// the profile in use and new ones, account sync, and the profile's own contents: level, currencies,
        /// weapon levels, warbonds and items by category (ProfileEdit). Only between sessions; the first
        /// change after the window opens backs the profile up where HellForge keeps its backups.</summary>
        sealed partial class View
        {
            ProfileData.Progression pe;
            List<ProfileData.Item> peCatalogue;
            Dictionary<uint, string> peNames;
            Dictionary<uint, string> peWarbondNames;
            ProfileData.GameText peText;
            Dictionary<uint, (string Slot, string Class)> peClasses;
            string peError;
            bool peLoading;
            readonly HashSet<string> peBackedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // HD2QD_HELLFORGE_OPEN=9,7 (pictures only): the categories shown open
            readonly HashSet<int> peOpen = new HashSet<int>((Environment.GetEnvironmentVariable("HD2QD_HELLFORGE_OPEN") ?? "")
                .Split(',').Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n >= 0));

            // categories of HellForge's item catalogue (progressionCategory) whose kind is known (research/item_names,
            // 2026-11-15: each from the items named in it); the others are shown by number with examples
            static readonly Dictionary<int, string> KnownCategories = new Dictionary<int, string>
            {
                [1] = "pe_cat_weapons", [2] = "pe_cat_attachments", [4] = "pe_cat_exosuit", [5] = "pe_cat_boosters",
                [7] = "pe_cat_emotes", [8] = "pe_cat_interactions", [9] = "pe_cat_capes", [11] = "pe_cat_stratagems", [23] = "pe_cat_titles",
            };
            // amounts, not things owned: edited as currencies (15 samples, 18 currencies) or not at all (21 Super Credit packs)
            static readonly HashSet<int> NotListed = new HashSet<int> { 15, 18, 21 };

            Hf.Profile CurrentProfile
            {
                get
                {
                    var p = profiles.FirstOrDefault(x => settings != null && x.Name == settings.Profile);
                    // HD2QD_HELLFORGE_PROFILE (pictures only): another profile folder, a copy, so every state can be shown
                    var other = Environment.GetEnvironmentVariable("HD2QD_HELLFORGE_PROFILE");
                    return p != null && !string.IsNullOrEmpty(other) ? new Hf.Profile { Name = p.Name, Template = p.Template, Selected = true, Folder = other } : p;
                }
            }

            void RenderProfile()
            {
                if (settings == null) return;
                RenderProfilePicker();
                RenderAccount();
                RenderEditor();
            }

            void LoadEditorData()
            {
                if (peLoading || install == null) return;
                peLoading = true;
                var inst = install;
                var h = home;
                var l = lang;
                Task.Run(() =>
                {
                    var cat = ProfileData.Catalogue(h, inst);
                    var prog = ProfileData.ReadProgression(inst);
                    var text = ProfileData.ReadText(Path.Combine(inst, "game", "data"), l);
                    var (community, warbonds) = ProfileData.Community();
                    var names = new Dictionary<uint, string>();
                    foreach (var kv in community)
                        if (text.Official(kv.Value) is string n) names[kv.Key] = n;
                    // the weapons as the game names them in a session (its language, the window's when they match)
                    foreach (var kv in ProfileData.Arsenal(inst)) if (!names.ContainsKey(kv.Key)) names[kv.Key] = kv.Value;
                    // a warbond: the community's name when its items match one, else named by one of its items
                    var wn = new Dictionary<uint, string>();
                    foreach (var w in prog.Warbonds)
                    {
                        var mine = new HashSet<uint>(w.Items.Select(i => i.MixId));
                        var match = warbonds.Select(c => (c.Name, Shared: c.Items.Count(mine.Contains))).OrderByDescending(x => x.Shared).FirstOrDefault();
                        if (match.Name != null && match.Shared >= 3) wn[w.Id] = text.Official(match.Name) ?? match.Name;
                        else if (w.Items.Select(i => names.TryGetValue(i.MixId, out var n) ? n : null).FirstOrDefault(n => n != null) is string item)
                            wn[w.Id] = T("pe_warbond_with", item);
                    }
                    var classes = ProfileData.WeaponClasses(h, inst);
                    return (cat, prog, text, names, wn, classes);
                }).ContinueWith(t =>
                {
                    peLoading = false;
                    if (t.IsFaulted) peError = t.Exception.InnerException?.Message ?? t.Exception.Message;
                    else (peCatalogue, pe, peText, peNames, peWarbondNames, peClasses) = t.Result;
                    if (tab == "profile") { Render(); Picture(); }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }

            string ItemName(uint mixId) => peNames != null && peNames.TryGetValue(mixId, out var n) ? n : null;

            /// <summary>The game's own word for a currency (its strings), else the window's.</summary>
            string Word(string english, string key) => peText?.Official(english) ?? T(key);

            void RenderEditor()
            {
                Section(T("pe_title"), T("pe_hint"));
                var profile = CurrentProfile;
                if (profile?.Folder == null || !File.Exists(ProfileEdit.File(profile.Folder))) { body.Children.Add(Small(T("pe_no_file"), new Thickness(0, 4, 0, 0))); return; }
                if (peError != null) { body.Children.Add(Line(T("pe_error", peError), "Yellow")); return; }
                if (pe == null) { LoadEditorData(); body.Children.Add(Small(T("pe_loading"), new Thickness(0, 4, 0, 0))); return; }
                ProfileEdit doc;
                try { doc = new ProfileEdit(profile.Folder); }
                catch (Exception e) when (e is HfError || e is IOException || e is UnauthorizedAccessException) { body.Children.Add(Line(T("pe_error", e.Message), "Yellow")); return; }
                if (Locked) body.Children.Add(Line(T("pe_locked"), "Yellow"));
                bool can = !Locked;

                // level
                Sub(T("pe_level"));
                int level = ProfileEdit.LevelOf(pe, doc.Experience);
                var lrow = Row();
                lrow.Children.Add(new TextBlock { Text = T("pe_level_now", level, doc.Experience.ToString("N0", CultureInfo.InvariantCulture)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) });
                var lbox = new TextBox { Style = S("Input"), Width = 80, Text = level.ToString(CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center, IsEnabled = can };
                lrow.Children.Add(lbox);
                var lset = Btn(T("pe_level_set", pe.Levels.Keys.Max()), false, () =>
                {
                    if (!int.TryParse(lbox.Text.Trim(), out var n) || !pe.Levels.ContainsKey(n)) { Tell(T("pe_level_bad", pe.Levels.Keys.Min(), pe.Levels.Keys.Max())); return; }
                    Apply(profile, e => e.SetLevel(pe, n));
                });
                lset.IsEnabled = can;
                lrow.Children.Add(lset);
                body.Children.Add(lrow);

                // currencies: the amounts, within the catalogue's caps
                Sub(T("pe_currencies"));
                var currencies = new List<(uint Id, string Name)> { (pe.MedalsConsumable, Word("Medal", "pe_medals")), (ProfileData.Requisition, Word("Requisition", "pe_requisition")) };
                var samples = peCatalogue.Where(i => i.Category == 15).ToList();
                foreach (var s in samples) currencies.Add((s.MixId, ItemName(s.MixId) ?? Word("Super Sample", "pe_sample")));
                currencies.Add((ProfileData.ForgeCredits, "Forge Credits"));
                var boxes = new List<(uint Id, TextBox Box, long Cap)>();
                var grid = new Grid { Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                foreach (var (id, name) in currencies)
                {
                    long cap = peCatalogue.FirstOrDefault(i => i.MixId == id)?.Cap ?? 0;
                    int r = grid.RowDefinitions.Count;
                    grid.RowDefinitions.Add(new RowDefinition());
                    var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
                    var box = new TextBox { Style = S("Input"), Width = 110, Text = doc.Held(id).ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 3, 0, 3), IsEnabled = can };
                    var capText = Small(cap > 0 ? T("pe_cap", cap.ToString("N0", CultureInfo.InvariantCulture)) : "", new Thickness(8, 0, 0, 0));
                    capText.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetRow(label, r); Grid.SetRow(box, r); Grid.SetRow(capText, r);
                    Grid.SetColumn(box, 1); Grid.SetColumn(capText, 2);
                    grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(capText);
                    boxes.Add((id, box, cap));
                }
                body.Children.Add(grid);
                var crow = Row();
                var csave = Btn(T("pe_save_currencies"), false, () =>
                {
                    var values = new List<(uint, long, long)>();
                    foreach (var (id, box, cap) in boxes)
                    {
                        if (!long.TryParse(box.Text.Trim().Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < 0) { Tell(T("pe_amount_bad", box.Text)); return; }
                        values.Add((id, v, cap));
                    }
                    Apply(profile, e => { foreach (var (id, v, cap) in values) e.SetAmount(id, v, cap); });
                });
                csave.IsEnabled = can;
                crow.Children.Add(csave);
                body.Children.Add(crow);

                // weapon levels
                Sub(T("pe_weapons"));
                body.Children.Add(Small(T("pe_weapons_hint"), new Thickness(0, 0, 0, 4)));
                var wall = Row();
                var wmax = Btn(T("pe_weapons_all_max"), false, () => Apply(profile, e => { foreach (var w in pe.Weapons) e.SetWeaponLevel(w, w.MaxLevel); }));
                wmax.IsEnabled = can && pe.Weapons.Any(w => doc.WeaponLevel(w.Id) < w.MaxLevel);
                wall.Children.Add(wmax);
                body.Children.Add(wall);
                // by the armory's own categories, in its order (the user, 2026-11-15), each under the game's word for it
                (string Slot, string Class) ClassOf(ProfileData.Package w) => peClasses != null && peClasses.TryGetValue(w.Weapon, out var c) ? c : (null, null);
                foreach (var group in pe.Weapons.GroupBy(ClassOf).OrderBy(g => ArmoryRank(g.Key.Slot, g.Key.Class)))
                {
                var heading = group.Key.Class == null ? T("pe_class_other")
                    : (peText?.Common(group.Key.Class) ?? group.Key.Class) + (group.Key.Slot == "secondary" ? T("pe_class_secondary") : "");
                body.Children.Add(new TextBlock { Text = heading, FontSize = 13, Foreground = B("Muted"), Margin = new Thickness(0, 10, 0, 2) });
                var wgrid = new WrapPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var w in group.OrderBy(w => ItemName(w.Weapon) ?? "￿" + w.Weapon.ToString("X8")))
                {
                    // the window's own controls (the user, 2026-11-15: no plain WPF ones): a Range slider and its
                    // value, set when the drag ends, as the Forge Credits slider is
                    var cell = new DockPanel { Width = 420, Margin = new Thickness(0, 3, 16, 3) };
                    int now = Math.Min(doc.WeaponLevel(w.Id), w.MaxLevel);
                    var value = new TextBlock { Text = now.ToString(CultureInfo.InvariantCulture), Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                    var slider = new Slider { Minimum = 0, Maximum = w.MaxLevel, SmallChange = 1, LargeChange = 5, TickFrequency = 1, IsSnapToTickEnabled = true, Value = now,
                                              Style = S("Range"), Width = 150, VerticalAlignment = VerticalAlignment.Center, IsEnabled = can, Margin = new Thickness(12, 0, 8, 0) };
                    slider.ValueChanged += (s, e) => value.Text = ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
                    var pkg = w;
                    void SetLevel() { int v = (int)slider.Value; if (v != now) { now = v; Apply(profile, ed => ed.SetWeaponLevel(pkg, v)); } }
                    slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, new System.Windows.Controls.Primitives.DragCompletedEventHandler((s, e) => SetLevel()));
                    slider.PreviewMouseLeftButtonUp += (s, e) => SetLevel();
                    DockPanel.SetDock(value, Dock.Right);
                    DockPanel.SetDock(slider, Dock.Right);
                    cell.Children.Add(value);
                    cell.Children.Add(slider);
                    cell.Children.Add(new TextBlock { Text = ItemName(w.Weapon) ?? T("pe_unnamed", w.Weapon.ToString("X8")), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                    wgrid.Children.Add(cell);
                }
                body.Children.Add(wgrid);
                }

                // warbonds: the ones the profile owns, up to a page
                Sub(Word("WARBONDS", "pe_warbonds"));
                body.Children.Add(Small(T("pe_warbonds_hint"), new Thickness(0, 0, 0, 4)));
                var bought = doc.Bought();
                foreach (var w in pe.Warbonds)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Width = 820, HorizontalAlignment = HorizontalAlignment.Left };
                    bool owns = doc.Owns(w);
                    int open = doc.PagesOpen(w), items = w.Items.Count(), have = w.Items.Count(i => bought.Contains(i.Id));
                    var right = new StackPanel { Orientation = Orientation.Horizontal };
                    if (owns && have < items)
                    {
                        // up to which page: the window's Segment choice, as a pack's variants are picked
                        int upTo = w.Pages.Count;
                        right.Children.Add(new TextBlock { Text = T("pe_pages_before"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                        for (int i = 1; i <= w.Pages.Count; i++)
                        {
                            int page = i;
                            var rb = new RadioButton { Content = page.ToString(CultureInfo.InvariantCulture), Style = S("Segment"), FontSize = 12, GroupName = "hfwb|" + w.Id,
                                                       IsChecked = page == upTo, IsEnabled = can, ToolTip = T("pe_page_n", page), MinWidth = 32 };
                            rb.Checked += (s, e) => upTo = page;
                            right.Children.Add(rb);
                        }
                        right.Children.Add(new TextBlock { Text = T("pe_pages_after"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
                        var wb = w;
                        var go = Btn(T("pe_unlock"), false, () => Apply(profile, e => e.UnlockWarbond(pe, wb, upTo)));
                        go.IsEnabled = can;
                        right.Children.Add(go);
                    }
                    DockPanel.SetDock(right, Dock.Right);
                    row.Children.Add(right);
                    var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
                    text.Children.Add(new TextBlock { Text = peWarbondNames.TryGetValue(w.Id, out var wn) ? wn : T("pe_warbond_n", w.Id.ToString("X8")) });
                    text.Children.Add(Small(owns ? T("pe_warbond_state", open, w.Pages.Count, have, items) : T("pe_warbond_not_owned"), new Thickness(0, 1, 0, 0)));
                    row.Children.Add(text);
                    body.Children.Add(row);
                }

                // items by category
                Sub(T("pe_items"));
                body.Children.Add(Small(T("pe_items_hint"), new Thickness(0, 0, 0, 4)));
                var held = doc.HeldIds();
                foreach (var g in peCatalogue.Where(i => !NotListed.Contains(i.Category) && !i.Consumable).GroupBy(i => i.Category).OrderBy(g => g.Key))
                {
                    var list = g.ToList();
                    int owned = list.Count(i => held.Contains(i.MixId)), named = list.Count(i => ItemName(i.MixId) != null);
                    string title = KnownCategories.TryGetValue(g.Key, out var key) ? T(key)
                        : T("pe_cat_n", g.Key) + (list.Select(i => ItemName(i.MixId)).FirstOrDefault(n => n != null) is string ex ? T("pe_cat_example", ex) : "");
                    var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Width = 820, HorizontalAlignment = HorizontalAlignment.Left };
                    var right = new StackPanel { Orientation = Orientation.Horizontal };
                    int cat = g.Key;
                    if (named > 0 && cat != 2)
                        right.Children.Add(Btn(peOpen.Contains(cat) ? T("pe_hide") : T("pe_show", named), false, () => { if (!peOpen.Remove(cat)) peOpen.Add(cat); Render(); }));
                    var all = Btn(T("pe_unlock_all"), false, () => Apply(profile, e => e.UnlockCategory(peCatalogue, cat)));
                    all.IsEnabled = can && owned < list.Count;
                    right.Children.Add(all);
                    DockPanel.SetDock(right, Dock.Right);
                    row.Children.Add(right);
                    var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
                    text.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
                    text.Children.Add(Small(T("pe_cat_state", owned, list.Count, named), new Thickness(0, 1, 0, 0)));
                    row.Children.Add(text);
                    body.Children.Add(row);
                    if (!peOpen.Contains(cat) || cat == 2) continue;
                    var names = new WrapPanel { Margin = new Thickness(16, 0, 0, 6), MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
                    foreach (var it in list.Where(i => ItemName(i.MixId) != null).OrderBy(i => ItemName(i.MixId), StringComparer.CurrentCulture))
                    {
                        bool has = held.Contains(it.MixId);
                        var cb = new CheckBox { Content = ItemName(it.MixId), Style = S("Tick"), IsChecked = has, IsEnabled = can && !has, Width = 280, Margin = new Thickness(0, 3, 8, 3), ToolTip = has ? T("pe_owned") : T("pe_add") };
                        var id = it.MixId;
                        cb.Click += (s, e) => Apply(profile, ed => ed.Grant(id));
                        names.Children.Add(cb);
                    }
                    body.Children.Add(names);
                }

                // backups: HellForge's, which its Account sync page lists too
                Sub(T("pe_backups"));
                var backups = ProfileEdit.Backups(profile.Folder);
                body.Children.Add(Small(backups.Count == 0 ? T("pe_backups_none") : T("pe_backups_hint"), new Thickness(0, 0, 0, 4)));
                foreach (var b in backups.Take(10))
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Width = 820, HorizontalAlignment = HorizontalAlignment.Left };
                    var path = b.Path;
                    var restore = Btn(T("pe_restore"), false, () =>
                    {
                        if (!Ask(T("pe_restore_ask", b.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), T("pe_restore"))) return;
                        Change(() => Local(() => ProfileEdit.Restore(profile.Folder, path, DateTime.UtcNow)));
                    });
                    restore.IsEnabled = can;
                    DockPanel.SetDock(restore, Dock.Right);
                    row.Children.Add(restore);
                    row.Children.Add(new TextBlock { Text = b.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  ·  " + b.Reason, VerticalAlignment = VerticalAlignment.Center });
                    body.Children.Add(row);
                }
            }

            // the armory's order of its weapon groups (HellForge's EditorCatalog.GroupOrder, the game's tabs)
            static readonly string[] PrimaryOrder = { "Assault Rifle", "Marksman Rifle", "Submachine Gun", "Shotgun", "Explosive", "Energy-Based", "Special" };
            static readonly string[] SecondaryOrder = { "Pistol", "Melee", "Special" };

            static int ArmoryRank(string slot, string cls)
            {
                if (cls == null) return 1000;
                if (slot == "primary") { int i = Array.IndexOf(PrimaryOrder, cls); return i >= 0 ? i : 50; }
                if (slot == "secondary") { int i = Array.IndexOf(SecondaryOrder, cls); return 100 + (i >= 0 ? i : 50); }
                return 500;
            }

            /// <summary>A part of the editor under its own small heading.</summary>
            void Sub(string title) =>
                body.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 2) });

            /// <summary>One change: the profile read afresh, changed and saved, after a backup the first time
            /// in this window. Never while a session runs (HellForge holds the profile then).</summary>
            void Apply(Hf.Profile profile, Action<ProfileEdit> edit)
            {
                if (Locked) return;
                var folder = profile.Folder;
                bool first = peBackedUp.Add(folder);
                Change(() => Local(() =>
                {
                    if (first) ProfileEdit.Backup(folder, DateTime.UtcNow);
                    var e = new ProfileEdit(folder);
                    edit(e);
                    e.Save();
                }));
            }
        }
    }
}
