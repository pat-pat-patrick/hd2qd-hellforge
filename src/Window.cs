using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hd2Qd.HellForge
{
    public static partial class Plugin
    {
        static Window open;

        /// <summary>The launcher window: the session (start, end, how it goes), HellForge's mods,
        /// the profile and settings, and the logs. One window at a time: opening again brings it
        /// forward and reads again.</summary>
        public static void Open(Window owner, Dictionary<string, object> host)
        {
            var lang = Words.Lang(host);
            if (open != null) { open.Activate(); ((View)open.Tag).Read(); return; }
            var v = new View(host, lang);
            Window w;
            if (host.TryGetValue("window", out var mk) && mk is Func<string, FrameworkElement, Window> make) w = make(Title(lang), v.Root);
            else w = new Window
            {
                Title = Title(lang) + " · HD2 Mod Quick Deploy", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = v.B("Ground900"), Foreground = v.B("Ink"), Content = v.Root, Icon = owner?.Icon,
                FontFamily = owner?.FontFamily ?? SystemFonts.MessageFontFamily,
            };
            w.Width = 1040; w.Height = 760; w.MinWidth = 760; w.MinHeight = 480;
            w.Tag = v;
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape && !(e.OriginalSource is TextBox)) w.Close(); };
            w.Closed += (s, e) => { v.Close(); open = null; };
            open = w;
            w.Show();
            v.Read();
        }

        sealed class View
        {
            readonly Dictionary<string, object> host;
            readonly string lang, home;
            public readonly DockPanel Root = new DockPanel { Margin = new Thickness(28, 20, 28, 20) };
            readonly StackPanel head = new StackPanel();
            readonly StackPanel tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            readonly StackPanel body = new StackPanel();
            readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            readonly DispatcherTimer lobbyPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };

            string install;
            Hf.Status status;
            Hf.ModList mods;
            Hf.Settings settings;
            List<Hf.Profile> profiles = new List<Hf.Profile>();
            List<string> templates = new List<string>();
            List<Hf.Session> runs = new List<Hf.Session>();
            List<Dictionary<string, object>> languages = new List<Dictionary<string, object>>();
            Dictionary<string, Placed.Entry> placed = new Dictionary<string, Placed.Entry>();
            string readError;
            string tab = "mods";
            bool busy, reading, adding, creatingProfile, syncedOnce, playWhenRead;
            HashSet<string> deployedPacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Quick Deploy's Launch on its HellForge target: a session once the window has read HellForge.</summary>
            public void PlayWhenRead()
            {
                if (!reading && status != null) { Play(); return; }
                playWhenRead = true;
            }

            // the session started from this window
            CancellationTokenSource playing;
            string playLine;
            bool stopAsked;
            readonly List<string> playProblems = new List<string>();

            public View(Dictionary<string, object> host, string lang)
            {
                this.host = host; this.lang = lang; home = Home(host);
                var top = new StackPanel();
                top.Children.Add(head);
                top.Children.Add(tabs);
                DockPanel.SetDock(top, Dock.Top);
                Root.Children.Add(top);
                Root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 14, 0, 0) });
                poll.Tick += (s, e) => Poll();
                lobbyPoll.Tick += (s, e) => { if (tab == "lobbies" && RunNow != null) RefreshLobbies(); };
                lobbyPoll.Start();
            }

            public void Close()
            {
                poll.Stop();
                lobbyPoll.Stop();
                playing?.Cancel();
            }

            public Brush B(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
            Style S(string key) => Application.Current?.TryFindResource(key) as Style;
            string T(string key) => Words.Get(lang, key);
            string T(string key, params object[] args) => Words.F(lang, key, args);

            bool Locked => status != null && status.Running || playing != null;

            /// <summary>The run folder of the session going on (the one started here, else the one
            /// status says runs), or null.</summary>
            string RunNow => playing != null ? playRun : status != null && status.Running ? status.LastRun : null;
            string playRun;
            /// <summary>The session's last status ("preparing", "observing", ...), from play's lines.</summary>
            string playStatus;
            /// <summary>While the session prepares: HellForge's build progress (its run folder's
            /// mod-builds.json), so a long first build says what it does (the user, 2026-11-12: the
            /// first code mod's start took 3 minutes, Zig downloaded, and nothing said so).</summary>
            readonly DispatcherTimer buildPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

            // ---------------------------------------------------------------- reading

            public async void Read()
            {
                if (reading) return;
                reading = true;
                int synced = 0;
                try
                {
                    var r = await Task.Run(() =>
                    {
                        var inst = Found(home, true);
                        if (inst == null) return (inst, (Exception)null, (Hf.Status)null);
                        try { return (inst, (Exception)null, Hf.ReadStatus(inst)); }
                        catch (Exception e) when (e is HfError || e is IOException || e is System.ComponentModel.Win32Exception) { return (inst, e, (Hf.Status)null); }
                    });
                    install = r.inst; status = r.Item3; readError = r.Item2?.Message;
                    if (install != null && status != null && status.GameCopy)
                    {
                        var inst = install;
                        var got = await Task.Run(() =>
                        {
                            try
                            {
                                // the library's changes brought over, the "only affects me" marks made again
                                if (!syncedOnce) { synced = Sync(home, inst, Data(host)).Count; syncedOnce = true; }
                                var m = Hf.ReadMods(inst);
                                var st = Hf.ParseSettings(Hf.Ask(inst, "settings"));
                                var pr = Hf.ParseProfiles(Hf.Ask(inst, "profiles"));
                                var ru = Hf.ParseRuns(Hf.Ask(inst, "logs"));
                                var la = Hf.Run(inst, "languages");
                                var langs = la.Ok ? Json.Objs(Json.Obj(la.Out), "languages") : new List<Dictionary<string, object>>();
                                deployedPacks = new HashSet<string>(Deployed.Load(home, inst).Values.Select(d => d.PackId).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
                                return (m, st, pr, ru, langs, Placed.Load(home, inst), (string)null);
                            }
                            catch (Exception e) when (e is HfError || e is IOException || e is UnauthorizedAccessException)
                            {
                                return ((Hf.ModList)null, (Hf.Settings)null, (new List<Hf.Profile>(), new List<string>()), new List<Hf.Session>(), new List<Dictionary<string, object>>(), new Dictionary<string, Placed.Entry>(), e.Message);
                            }
                        });
                        mods = got.Item1; settings = got.Item2; profiles = got.Item3.Item1; templates = got.Item3.Item2; runs = got.Item4; languages = got.Item5; placed = got.Item6;
                        readError = got.Item7;
                        if (synced > 0) { Foot(T("synced", synced)); Changed(); }
                    }
                }
                finally { reading = false; }
                Render();
                if (install != null) poll.Start();
                if (playWhenRead) { playWhenRead = false; Play(); }
            }

            /// <summary>Every 3 s while the window is open: whether a session started or ended
            /// (HellForge.exe may start one too).</summary>
            async void Poll()
            {
                if (reading || busy || install == null || playing != null) return;
                var inst = install;
                Hf.Status s;
                try { s = await Task.Run(() => Hf.ReadStatus(inst)); }
                catch (Exception e) when (e is HfError || e is IOException) { return; }
                if (s == null || status == null) return;
                bool changed = s.Running != status.Running || s.LastRun != status.LastRun || s.LastStatus != status.LastStatus;
                status = s;
                if (changed) Read();
            }

            // ---------------------------------------------------------------- drawing

            void Render()
            {
                RenderHead();
                RenderTabs();
                body.Children.Clear();
                if (install == null) { RenderNotFound(); return; }
                if (status == null) { body.Children.Add(Line(T("cannot_read", readError ?? ""), "Red")); return; }
                if (!status.GameCopy) { RenderNotReady(); return; }
                if (readError != null) body.Children.Add(Line(T("cannot_read", readError), "Red"));
                switch (tab)
                {
                    case "settings": RenderSettings(); break;
                    case "logs": RenderLogs(); break;
                    case "lobbies": RenderLobbies(); break;
                    default: RenderMods(); break;
                }
            }

            void RenderHead()
            {
                head.Children.Clear();
                var top = new DockPanel();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                if (install != null)
                {
                    buttons.Children.Add(Btn(T("open_hellforge"), false, () => OpenLauncher()));
                    if (status != null && status.GameCopy)
                    {
                        if (Locked)
                            buttons.Children.Add(Btn(stopAsked ? T("stop_now") : T("stop"), true, () => Stop()));
                        else
                            buttons.Children.Add(Btn(T("play"), true, () => Play()));
                    }
                }
                DockPanel.SetDock(buttons, Dock.Right);
                top.Children.Add(buttons);
                var facts = new StackPanel();
                if (install != null && status != null)
                {
                    facts.Children.Add(new TextBlock { Text = T("versions", status.HellForge ?? "?", status.GameBuild ?? "?"), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                    facts.Children.Add(Small(install, new Thickness(0, 2, 16, 0)));
                }
                else if (install != null) facts.Children.Add(Small(install, new Thickness(0, 4, 16, 0)));
                else facts.Children.Add(Small(T("intro"), new Thickness(0, 4, 16, 0)));
                top.Children.Add(facts);
                head.Children.Add(top);
                if (install != null && status != null && status.GameCopy)
                    head.Children.Add(new TextBlock { Text = SessionLine(), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Locked ? B("Yellow") : B("Muted") });
                foreach (var p in playProblems)
                    head.Children.Add(Line(p, "Red"));
            }

            string SessionLine()
            {
                if (playing != null) return playLine ?? T("play_starting");
                if (status.Running) return T("session_running");
                if (playLine != null) return playLine;
                var last = runs.FirstOrDefault();
                return last == null ? T("session_none") : T("session_last", When(last.Started), Ending(last));
            }

            void RenderTabs()
            {
                tabs.Children.Clear();
                if (install == null || status == null || !status.GameCopy) return;
                foreach (var (key, text) in new[] { ("mods", T("tab_mods")), ("lobbies", T("tab_lobbies")), ("settings", T("tab_settings")), ("logs", T("tab_logs")) })
                {
                    var rb = new RadioButton { Content = text, Style = S("Segment"), IsChecked = tab == key, GroupName = "hftab" };
                    rb.Checked += (s, e) => { if (tab != key) { tab = key; Render(); if (key == "lobbies" && RunNow != null) RefreshLobbies(); } };
                    tabs.Children.Add(rb);
                }
            }

            void RenderNotFound()
            {
                body.Children.Add(Para(T("not_found")));
                var row = Row();
                row.Children.Add(Btn(T("choose_folder"), true, ChooseFolder));
                row.Children.Add(Btn(T("look_again"), false, Read));
                body.Children.Add(row);
            }

            void RenderNotReady()
            {
                body.Children.Add(Para(T("not_ready")));
                var row = Row();
                row.Children.Add(Btn(T("open_hellforge_setup"), true, () => OpenLauncher()));
                row.Children.Add(Btn(T("look_again"), false, Read));
                body.Children.Add(row);
            }

            // ---------------------------------------------------------------- MODs

            void RenderMods()
            {
                if (mods == null) return;
                if (Locked) body.Children.Add(Line(T("locked"), "Yellow"));
                var code = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                code.Children.Add(new TextBlock { Text = T("modlist") + "  ", VerticalAlignment = VerticalAlignment.Center, Foreground = B("Muted") });
                if (mods.Modlist != null)
                    code.Children.Add(new TextBox { Text = mods.Modlist, Style = S("Copyable"), IsReadOnly = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = T("modlist_tip") });
                else code.Children.Add(new TextBlock { Text = T("modlist_none"), VerticalAlignment = VerticalAlignment.Center });
                body.Children.Add(code);
                foreach (var p in mods.Problems) body.Children.Add(Line(p, "Red"));
                bool luaOn = mods.Mods.Any(m => m.Enabled && m.Has("lua") && !IsBingus(m));
                bool bingusOn = mods.Mods.Any(m => m.Enabled && IsBingus(m));
                if (luaOn && !bingusOn) body.Children.Add(Line(T("no_bingus"), "Yellow"));

                RenderAllOnlyMe();

                // a manager with deploy targets hands host["target"]; an older one (0.19.0 and
                // earlier) has no HellForge choice above its library to point at
                body.Children.Add(Small(T(host.ContainsKey("target") ? "manage_in_manager" : "manage_needs_newer"), new Thickness(0, 6, 0, 4)));

                var shown = mods.Mods.Where(m => !m.Hidden).ToList();
                foreach (var (group, title, hint) in new[]
                {
                    ("gameplay", T("group_gameplay"), T("group_gameplay_hint")),
                    ("per-player", T("group_per_player"), T("group_per_player_hint")),
                    ("cosmetic", T("group_cosmetic"), T("group_cosmetic_hint")),
                })
                {
                    var inGroup = shown.Where(m => m.Group == group).ToList();
                    if (inGroup.Count == 0) continue;
                    body.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 0) });
                    body.Children.Add(Small(hint, new Thickness(0, 2, 0, 6)));
                    for (int i = 0; i < inGroup.Count; i++) body.Children.Add(ModRow(inGroup[i], i > 0 ? inGroup[i - 1] : null, i + 1 < inGroup.Count ? inGroup[i + 1] : null));
                }
                var other = shown.Where(m => m.Group != "gameplay" && m.Group != "per-player" && m.Group != "cosmetic").ToList();
                foreach (var m in other) body.Children.Add(ModRow(m, null, null));
            }

            /// <summary>The setting "every gameplay mod only affects me" (Coop.SetAll), and the mods
            /// HellForge would not take that way.</summary>
            void RenderAllOnlyMe()
            {
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 4), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
                var sw = new CheckBox { Style = S("Switch"), IsChecked = Coop.AllOn(home), IsEnabled = !Locked, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 16, 0) };
                sw.Click += (s, e) => AllOnlyMe(sw.IsChecked == true);
                DockPanel.SetDock(sw, Dock.Left);
                row.Children.Add(sw);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = T("all_only_me"), FontSize = 14, FontWeight = FontWeights.SemiBold });
                text.Children.Add(Small(T("all_only_me_hint"), new Thickness(0, 2, 0, 0)));
                if (Coop.AllOn(home))
                    foreach (var r in Coop.Refused(home).Values)
                        text.Children.Add(new TextBlock { Text = T("all_only_me_refused", r), FontSize = 12, Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                row.Children.Add(text);
                body.Children.Add(row);
            }

            async void AllOnlyMe(bool on)
            {
                if (on && !Ask(T("all_only_me_ask"), T("all_only_me_go"))) { Render(); return; }
                var inst = install;
                List<string> refusedNow = null;
                int marked = 0;
                await Do(() =>
                {
                    var r = Coop.SetAll(home, inst, on);
                    marked = r.Marked.Count; refusedNow = r.Refused;
                    return new Hf.Result();
                }, null);
                if (refusedNow == null) return;
                Foot(on ? T("all_only_me_done", marked) : T("all_only_me_off"));
                if (refusedNow.Count > 0) Tell(T("all_only_me_refused_tell") + "\n" + string.Join("\n", refusedNow));
            }

            static bool IsBingus(Hf.Mod m) => m.Folder != null && File.Exists(Path.Combine(m.Folder, "BingusSharedLoader-manifest.json"));

            Placed.Entry PlacedOf(Hf.Mod m) => placed.Values.FirstOrDefault(e => string.Equals(e.Id, m.Id, StringComparison.OrdinalIgnoreCase));

            FrameworkElement ModRow(Hf.Mod m, Hf.Mod above, Hf.Mod below)
            {
                var g = new Grid { MinHeight = 52 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                void Cell(int col, UIElement e) { Grid.SetColumn(e, col); g.Children.Add(e); }

                bool managed = deployedPacks.Contains(m.Id);
                var sw = new CheckBox { Style = S("Switch"), IsChecked = m.Enabled, VerticalAlignment = VerticalAlignment.Center, IsEnabled = !Locked && !m.Has("local-stats") && !managed,
                                        ToolTip = managed ? T("managed") : m.Enabled ? T("switch_off") : T("switch_on") };
                sw.Click += (s, e) => Change(() => Hf.Run(install, "mod-switch", "--id", m.Id, "--on", sw.IsChecked == true ? "yes" : "no"));
                Cell(0, sw);

                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6) };
                var title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14 };
                title.Inlines.Add(new System.Windows.Documents.Run(m.Name ?? m.Id) { Foreground = m.Enabled ? B("Ink") : B("Muted") });
                if (!string.IsNullOrEmpty(m.Version)) title.Inlines.Add(new System.Windows.Documents.Run("  " + m.Version) { Foreground = B("Muted"), FontSize = 12 });
                names.Children.Add(title);
                var marks = new List<string>();
                var mine = PlacedOf(m);
                if (mine != null || managed) marks.Add(T("mark_library"));
                if (m.Has("bundled")) marks.Add(T("mark_bundled"));
                if (m.Has("lua")) marks.Add("LUA");
                if (m.Has("code")) marks.Add(T("mark_code"));
                if (m.Has("stats")) marks.Add(T("mark_stats"));
                if (marks.Count > 0) names.Children.Add(new TextBlock { Text = string.Join("  ·  ", marks), FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(0, 2, 0, 0) });
                if (mine != null)
                {
                    bool ported = mine.Level != null && mine.Level != Port.Level.Unknown.ToString();
                    foreach (var (t, c) in PortLines(lang, mine))
                        names.Children.Add(new TextBlock { Text = t, FontSize = 12, Foreground = B(c), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                    foreach (var r in Risks(lang, Scan.Of(mine.Library ?? ""), ported))
                        names.Children.Add(new TextBlock { Text = r, FontSize = 12, Foreground = B("Yellow"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                }
                if (!managed && (m.Options.Count > 1 || m.Options.Any(o => o.Variants.Count > 0))) names.Children.Add(Options(m));
                Cell(1, names);

                if (Coop.Applies(m))
                {
                    var only = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), ToolTip = T("only_me_tip") };
                    var c = new CheckBox { Style = S("Switch"), IsChecked = Coop.IsPerPlayer(m), IsEnabled = !Locked, VerticalAlignment = VerticalAlignment.Center };
                    c.Click += (s, e) => OnlyMe(m, c.IsChecked == true);
                    only.Children.Add(c);
                    only.Children.Add(new TextBlock { Text = T("only_me"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                    Cell(2, only);
                }

                var order = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                order.Children.Add(Glyph("", T("move_up"), above != null && !Locked && !managed && !deployedPacks.Contains(above.Id), () => Move(m, above)));
                order.Children.Add(Glyph("", T("move_down"), below != null && !Locked && !managed && !deployedPacks.Contains(below.Id), () => Move(below, m)));
                Cell(3, order);

                if (mine != null)
                {
                    var remove = Btn(T("remove"), false, () => Remove(mine));
                    remove.IsEnabled = !Locked;
                    remove.Margin = new Thickness(8, 0, 0, 0);
                    Cell(4, remove);
                }
                return new Border { Child = g, BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1) };
            }

            /// <summary>A pack's options, as HellForge's pack picker: each option on or off, and an
            /// option's variant (one of them).</summary>
            FrameworkElement Options(Hf.Mod m)
            {
                var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var o in m.Options)
                {
                    var box = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
                    var tick = new CheckBox { Content = o.Name, Style = S("Tick"), IsChecked = o.On, IsEnabled = !Locked, VerticalAlignment = VerticalAlignment.Center };
                    tick.Click += (s, e) => Change(() => Hf.Run(install, "mod-pack", "--id", m.Id, "--option", o.Name, "--on", tick.IsChecked == true ? "yes" : "no"));
                    box.Children.Add(tick);
                    if (o.Variants.Count > 0)
                    {
                        var picked = o.Picked ?? o.Variants[0];
                        foreach (var v in o.Variants)
                        {
                            var rb = new RadioButton { Content = v, Style = S("Segment"), FontSize = 12, IsChecked = v == picked, IsEnabled = !Locked && o.On, GroupName = m.Id + "|" + o.Name };
                            rb.Checked += (s, e) => { if (v != picked) Change(() => Hf.Run(install, "mod-pack", "--id", m.Id, "--option", o.Name, "--variant", v)); };
                            box.Children.Add(rb);
                        }
                    }
                    panel.Children.Add(box);
                }
                return panel;
            }

            /// <summary>upper takes lower's place and lower goes above it: the whole list's new
            /// order (top first) to mod-order, so nothing else moves.</summary>
            void Move(Hf.Mod lower, Hf.Mod upper)
            {
                var ids = mods.Mods.Select(x => x.Id).ToList();
                int a = ids.IndexOf(upper.Id), b = ids.IndexOf(lower.Id);
                if (a < 0 || b < 0) return;
                ids[a] = lower.Id; ids[b] = upper.Id;
                Change(() => Hf.Run(install, "mod-order", "--ids", string.Join(",", ids)));
            }

            void OnlyMe(Hf.Mod m, bool on)
            {
                if (on && !Ask(T("only_me_ask", m.Name), T("only_me"))) { Render(); return; }
                var inst = install;
                Change(() =>
                {
                    Coop.Set(home, inst, m, on);
                    var after = Hf.ReadMods(inst);
                    var problem = after.Problems.FirstOrDefault(p => p.IndexOf(m.Name, StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf(m.Id, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (on && problem != null)
                    {
                        // HellForge refuses per-player for a mod that changes what every machine shares
                        Coop.Set(home, inst, after.Mods.FirstOrDefault(x => x.Id == m.Id) ?? m, false);
                        return new Hf.Result { Code = 1, Err = "HellForge: " + problem };
                    }
                    return new Hf.Result();
                });
            }

            async void Remove(Placed.Entry e)
            {
                if (!Ask(T("remove_ask", e.Name), T("remove"))) return;
                var inst = install;
                await Do(() => { Placed.Remove(home, inst, e); return new Hf.Result(); }, T("removed", e.Name));
            }

            /// <summary>One change through HellForge, then everything read again.</summary>
            async void Change(Func<Hf.Result> run) => await Do(run, null);

            async Task Do(Func<Hf.Result> run, string done)
            {
                if (busy) return;
                busy = true;
                Hf.Result r;
                try { r = await Task.Run(run); }
                catch (Exception e) when (e is HfError || e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
                {
                    r = new Hf.Result { Code = 1, Err = e is HfError h ? Refused(lang, h) : e.Message };
                }
                finally { busy = false; }
                if (!r.Ok) Tell(T("refused", r.Why));
                else if (done != null) Foot(done);
                Changed();
                Read();
            }

            // ---------------------------------------------------------------- adding from the library

            /// <summary>A library mod as the add panel shows it: what HellForge would call it, and the
            /// mod of that id HellForge already has from elsewhere (never replaced).</summary>
            sealed class LibMod
            {
                public string Name, Path, Id, Clash;
                public bool Enabled;
                public Scan.Facts Facts;
                /// <summary>What HellForge's copy would be (null: not compared).</summary>
                public Port.Report Port;
            }

            List<LibMod> library;
            readonly HashSet<string> picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void RenderAdd()
            {
                var box = new Border { BorderBrush = B("Grid"), BorderThickness = new Thickness(1), Padding = new Thickness(16), Margin = new Thickness(0, 4, 0, 8) };
                var panel = new StackPanel();
                box.Child = panel;
                body.Children.Add(box);
                panel.Children.Add(Small(T("add_intro"), new Thickness(0, 0, 0, 8)));
                if (library == null)
                {
                    panel.Children.Add(new TextBlock { Text = T("port_reading") });
                    LoadLibrary();
                    return;
                }
                var candidates = library.Where(l => Placed.Of(placed, l.Path) == null)
                    .OrderBy(l => l.Id != null && l.Clash == null ? 0 : 1).ThenByDescending(l => l.Enabled).ThenBy(l => l.Name).ToList();
                if (candidates.Count == 0) { panel.Children.Add(new TextBlock { Text = T("add_none") }); return; }
                foreach (var l in candidates)
                {
                    var row = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
                    bool can = l.Id != null && l.Clash == null;
                    var tick = new CheckBox { Content = l.Name, Style = S("Tick"), IsChecked = can && picked.Contains(l.Path), IsEnabled = can };
                    tick.Click += (s, e) => { if (tick.IsChecked == true) picked.Add(l.Path); else picked.Remove(l.Path); };
                    row.Children.Add(tick);
                    var muted = new List<string>();
                    if (l.Id == null) muted.Add(T("refuse_not_a_mod"));
                    else if (l.Clash != null) muted.Add(T("add_there_already", l.Clash));
                    else if (!l.Enabled) muted.Add(T("add_off_in_library"));
                    foreach (var n in muted) row.Children.Add(new TextBlock { Text = n, FontSize = 12, Foreground = B("Muted"), Margin = new Thickness(32, 0, 0, 2), TextWrapping = TextWrapping.Wrap });
                    if (can)
                    {
                        foreach (var (t, c) in PortLines(lang, l.Port))
                            row.Children.Add(new TextBlock { Text = t, FontSize = 12, Foreground = B(c), Margin = new Thickness(32, 0, 0, 2), TextWrapping = TextWrapping.Wrap });
                        foreach (var n in Risks(lang, l.Facts, l.Port != null && l.Port.Level != HellForge.Port.Level.Unknown))
                            row.Children.Add(new TextBlock { Text = n, FontSize = 12, Foreground = B("Yellow"), Margin = new Thickness(32, 0, 0, 2), TextWrapping = TextWrapping.Wrap });
                    }
                    panel.Children.Add(row);
                }
                var go = Btn(T("add_go"), true, () => AddPicked());
                go.Margin = new Thickness(0, 10, 0, 0);
                go.HorizontalAlignment = HorizontalAlignment.Left;
                panel.Children.Add(go);
            }

            async void LoadLibrary()
            {
                try
                {
                    var current = mods;
                    var inst = install;
                    var official = Data(host);
                    library = await Task.Run(() =>
                    {
                        var list = Library(host).Select(l =>
                        {
                            var id = Directory.Exists(l.Path) ? Placed.IdOf(l.Path, Placed.FolderName(l.Name)) : null;
                            var clash = id == null || current == null ? null
                                : current.Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase) && PlacedOf(m) == null)?.Name;
                            return new LibMod { Name = l.Name, Path = l.Path, Enabled = l.Enabled, Id = id, Clash = clash, Facts = Scan.Of(l.Path) };
                        }).ToList();
                        // what each would become on HellForge: the two games compared once for all
                        try
                        {
                            var reports = HellForge.Port.CheckMany(home, official, Path.Combine(inst, "game", "data"), list.Where(l => l.Id != null && l.Clash == null).Select(l => l.Path));
                            foreach (var l in list) if (reports.TryGetValue(l.Path, out var r)) l.Port = r;
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException) { }
                        return list;
                    });
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
                {
                    library = new List<LibMod>();
                    Tell(e.Message);
                }
                if (adding && tab == "mods") Render();
            }

            async void AddPicked()
            {
                var chosen = library.Where(l => picked.Contains(l.Path)).ToList();
                if (chosen.Count == 0) return;
                var risky = chosen.Where(c => c.Facts.Any).ToList();
                if (risky.Count > 0 && !Ask(T("add_risky_ask", string.Join("\n", risky.Select(r => r.Name))), T("add_go"))) return;
                var inst = install;
                var refused = new List<string>();
                int added = 0;
                await Do(() =>
                {
                    var list = Hf.ReadMods(inst);
                    foreach (var c in chosen)
                    {
                        try { Placed.Put(home, inst, c.Name, c.Path, list, Data(host)); added++; list = Hf.ReadMods(inst); }
                        catch (HfError e) { refused.Add(c.Name + ": " + Refused(lang, e)); }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { refused.Add(c.Name + ": " + e.Message); }
                    }
                    return new Hf.Result();
                }, null);
                picked.Clear();
                Foot(T("added", added));
                if (refused.Count > 0) Tell(T("add_refused") + "\n" + string.Join("\n", refused));
                if (refused.Count == 0) { adding = false; Render(); }
            }

            // ---------------------------------------------------------------- profile and settings

            void RenderSettings()
            {
                if (settings == null) return;
                Section(T("profile"), T("profile_hint"));
                var prow = new WrapPanel();
                foreach (var p in profiles)
                {
                    var rb = new RadioButton { Content = p.Name + (p.Template != null ? "  (" + p.Template + ")" : ""), Style = S("Segment"), GroupName = "hfprofile", IsChecked = p.Name == settings.Profile, IsEnabled = !Locked };
                    rb.Checked += (s, e) => { if (p.Name != settings.Profile) Change(() => Hf.Run(install, "settings", "--profile", p.Name)); };
                    prow.Children.Add(rb);
                }
                var create = Btn(T("profile_new"), false, () => { creatingProfile = !creatingProfile; Render(); });
                create.Margin = new Thickness(8, 2, 0, 2);
                prow.Children.Add(create);
                body.Children.Add(prow);
                if (creatingProfile) body.Children.Add(NewProfile());

                Section(T("war"), T("war_hint"));
                var wrow = new WrapPanel();
                foreach (var (key, text) in new[] { ("frozen", T("war_frozen")), ("live", T("war_live")), ("open", T("war_open")) })
                {
                    var rb = new RadioButton { Content = text, Style = S("Segment"), GroupName = "hfwar", IsChecked = settings.War == key };
                    rb.Checked += (s, e) => { if (settings.War != key) Change(() => Hf.Run(install, "settings", "--war", key)); };
                    wrow.Children.Add(rb);
                }
                body.Children.Add(wrow);

                Toggle(T("multiplayer"), T("multiplayer_hint"), settings.Multiplayer, on => Hf.Run(install, "settings", "--multiplayer", on ? "yes" : "no"));
                Toggle(T("strangers"), T("strangers_hint"), settings.Strangers, on =>
                    Hf.Run(install, "settings", "--strangers", on ? "yes" : "no"), on => !on || Ask(T("strangers_ask"), T("strangers_on")));
                Toggle(T("sandbox"), T("sandbox_hint"), settings.Sandbox, on => Hf.Run(install, "settings", "--sandbox", on ? "yes" : "no"));
                Toggle(T("live_changes"), T("live_changes_hint"), settings.LiveChanges, on => Hf.Run(install, "settings", "--live-changes", on ? "yes" : "no"));

                Section(T("forge_credits"), T("forge_credits_hint"));
                var fc = new DockPanel { Margin = new Thickness(0, 4, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
                var value = new TextBlock { Text = settings.ForgeCredits + " %", Width = 56, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(value, Dock.Right);
                fc.Children.Add(value);
                var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 10, TickFrequency = 5, IsSnapToTickEnabled = true, Value = settings.ForgeCredits, Style = S("Range"), Width = 400, VerticalAlignment = VerticalAlignment.Center };
                slider.ValueChanged += (s, e) => value.Text = (int)slider.Value + " %";
                slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) => SetCredits((int)slider.Value)));
                slider.PreviewMouseLeftButtonUp += (s, e) => SetCredits((int)slider.Value);
                fc.Children.Add(slider);
                body.Children.Add(fc);

                Section(T("account"), T("account_hint"));
                if (Proxy.On()) body.Children.Add(Line(T("proxy_on"), "Yellow"));
                var arow = Row();
                arow.Children.Add(Btn(T("account_sync"), false, SyncAccount));
                body.Children.Add(arow);

                var packs = languages.Where(l => !Json.Bool(l, "inCopy")).ToList();
                var inCopy = languages.Where(l => Json.Bool(l, "inCopy")).Select(l => Json.Str(l, "name")).ToList();
                Section(T("languages"), inCopy.Count > 0 ? T("languages_in", string.Join(", ", inCopy)) : T("languages_hint"));
                foreach (var l in packs)
                {
                    var name = Json.Str(l, "name");
                    var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
                    if (Json.Bool(l, "installedInSteam"))
                    {
                        var add = Btn(T("language_add"), false, () => Change(() => Hf.Run(install, "language-add", "--name", name)));
                        add.IsEnabled = !Locked;
                        DockPanel.SetDock(add, Dock.Right);
                        row.Children.Add(add);
                        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                    }
                    else
                    {
                        var cmd = Json.Str(l, "command") ?? "";
                        var st = new StackPanel();
                        st.Children.Add(new TextBlock { Text = name + "  ·  " + T("language_steam"), TextWrapping = TextWrapping.Wrap });
                        st.Children.Add(new TextBox { Text = cmd, Style = S("Copyable"), IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) });
                        row.Children.Add(st);
                    }
                    body.Children.Add(row);
                }
            }

            int creditsSent = -1;

            void SetCredits(int v)
            {
                if (settings == null || v == settings.ForgeCredits || v == creditsSent) return;
                creditsSent = v;
                Change(() => Hf.Run(install, "settings", "--forge-credits", v.ToString(CultureInfo.InvariantCulture)));
            }

            FrameworkElement NewProfile()
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                var name = new TextBox { Style = S("Input"), Width = 220, VerticalAlignment = VerticalAlignment.Center, ToolTip = T("profile_name_rule") };
                row.Children.Add(name);
                string template = templates.FirstOrDefault(t => t == "starter") ?? templates.FirstOrDefault();
                foreach (var t in templates)
                {
                    var rb = new RadioButton { Content = T("template_" + t) == "template_" + t ? t : T("template_" + t), Style = S("Segment"), GroupName = "hftemplate", IsChecked = t == template, Margin = new Thickness(8, 0, 0, 0) };
                    rb.Checked += (s, e) => template = t;
                    row.Children.Add(rb);
                }
                var go = Btn(T("profile_create"), true, () =>
                {
                    var n = name.Text.Trim();
                    if (!Hf.GoodProfileName(n)) { Tell(T("profile_name_rule")); return; }
                    creatingProfile = false;
                    Change(() => Hf.Run(install, "profiles", "--create", n, "--template", template));
                });
                go.Margin = new Thickness(12, 0, 0, 0);
                row.Children.Add(go);
                return row;
            }

            void Toggle(string label, string hint, bool on, Func<bool, Hf.Result> set, Func<bool, bool> confirm = null)
            {
                var row = new DockPanel { Margin = new Thickness(0, 16, 0, 0), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
                var sw = new CheckBox { Style = S("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 16, 0) };
                sw.Click += (s, e) =>
                {
                    bool want = sw.IsChecked == true;
                    if (confirm != null && !confirm(want)) { sw.IsChecked = !want; return; }
                    Change(() => set(want));
                };
                DockPanel.SetDock(sw, Dock.Left);
                row.Children.Add(sw);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold });
                text.Children.Add(Small(hint, new Thickness(0, 2, 0, 0)));
                row.Children.Add(text);
                body.Children.Add(row);
            }

            void Section(string title, string hint)
            {
                body.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 22, 0, 0) });
                if (!string.IsNullOrEmpty(hint)) body.Children.Add(Small(hint, new Thickness(0, 2, 0, 6)));
            }

            // ---------------------------------------------------------------- lobbies

            Lobbies.Listing lobbies;
            string lobbySignature, lobbyError, lobbyNote;
            bool lobbyReading;

            /// <summary>The lobby list read again (the API searches at most every 2 s; this asks
            /// every 4 s while the tab is shown). Drawn again only when it changed.</summary>
            async void RefreshLobbies()
            {
                if (lobbyReading) return;
                var run = RunNow;
                var backend = Lobbies.BackendUrl(run);
                if (backend == null)
                {
                    if (lobbyError != T("lobbies_starting")) { lobbyError = T("lobbies_starting"); lobbies = null; if (tab == "lobbies") Render(); }
                    return;
                }
                lobbyReading = true;
                Lobbies.Listing got = null;
                string error = null;
                try { got = await Task.Run(() => Lobbies.List(backend)); }
                catch (HfError e) { error = e.Message; }
                finally { lobbyReading = false; }
                var signature = got == null ? "error:" + error
                    : Json.Write(new object[] { got.Available, got.Reason, got.Lobbies.Select(l => l.Id + l.Verdict + l.Members + l.Reason + l.InMission + l.PlanetId + l.Difficulty).ToArray(), got.Needed.Select(n => n.Id + n.Lobbies).ToArray() });
                if (got != null) lobbies = got;
                lobbyError = error;
                if (signature != lobbySignature && tab == "lobbies") { lobbySignature = signature; Render(); }
            }

            void RenderLobbies()
            {
                var run = RunNow;
                if (run == null)
                {
                    body.Children.Add(Para(T("lobbies_need_session")));
                    body.Children.Add(Small(T("lobbies_game_filter"), new Thickness(0, 0, 0, 8)));
                    return;
                }
                if (lobbies == null)
                {
                    body.Children.Add(Para(lobbyError ?? T("reading")));
                    return;
                }
                if (lobbyError != null) body.Children.Add(Line(T("lobbies_error", lobbyError), "Yellow"));
                if (!lobbies.Available)
                {
                    body.Children.Add(Line(T("lobbies_unavailable", lobbies.Reason ?? ""), "Yellow"));
                    return;
                }
                int now = lobbies.Lobbies.Count(l => l.Joinable);
                var top = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                var quick = Btn(T("quick_join"), true, QuickJoin);
                quick.IsEnabled = now > 0;
                quick.ToolTip = T("quick_join_tip");
                buttons.Children.Add(quick);
                buttons.Children.Add(Btn(T("refresh"), false, () => { lobbySignature = null; RefreshLobbies(); }));
                DockPanel.SetDock(buttons, Dock.Right);
                top.Children.Add(buttons);
                top.Children.Add(new TextBlock { Text = T("lobbies_count", lobbies.Lobbies.Count, now), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(top);
                body.Children.Add(Small(T("lobbies_game_filter"), new Thickness(0, 2, 0, 8)));
                if (lobbyNote != null) body.Children.Add(Line(lobbyNote, "Blue"));

                if (lobbies.Needed.Count > 0)
                {
                    Section(T("needed"), T("needed_hint"));
                    foreach (var n in lobbies.Needed)
                    {
                        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
                        var link = WebLink(n.Url);
                        if (link != null)
                        {
                            var open = Btn(T("open_page"), false, () => OpenWeb(link));
                            open.ToolTip = link;
                            DockPanel.SetDock(open, Dock.Right);
                            row.Children.Add(open);
                        }
                        row.Children.Add(new TextBlock
                        {
                            Text = (n.Name ?? n.Id) + (n.Versions.Count > 0 ? "  " + string.Join(", ", n.Versions) : "") + "  ·  " + T("needed_by", n.Lobbies),
                            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                        });
                        body.Children.Add(row);
                    }
                }

                if (lobbies.Lobbies.Count == 0) { body.Children.Add(Para(T("lobbies_none"))); return; }
                Section(T("lobbies"), null);
                foreach (var l in lobbies.Lobbies) body.Children.Add(LobbyRow(l));
            }

            FrameworkElement LobbyRow(Lobbies.Lobby l)
            {
                var g = new DockPanel { MinHeight = 56, Margin = new Thickness(0, 4, 0, 4) };
                if (l.Joinable)
                {
                    var join = Btn(T("join"), true, () => Join(l));
                    join.VerticalAlignment = VerticalAlignment.Center;
                    DockPanel.SetDock(join, Dock.Right);
                    g.Children.Add(join);
                }
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var title = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
                title.Inlines.Add(new System.Windows.Documents.Run(l.HostName ?? T("unknown_host")) { FontWeight = FontWeights.SemiBold });
                var facts = new List<string>();
                if (l.Friend) facts.Add(T("lobby_friend"));
                facts.Add(T("lobby_members", l.Members, l.MaxMembers));
                var race = Lobbies.RaceKey(l.Race);
                if (race != null) facts.Add(T(race));
                if (l.PlanetId != null) facts.Add(T("lobby_planet", l.PlanetId));
                if (l.Difficulty != null) facts.Add(T("lobby_difficulty", l.Difficulty));
                facts.Add(l.InMission ? T("lobby_in_mission") : T("lobby_on_ship"));
                if (l.Older) facts.Add(T("lobby_older"));
                title.Inlines.Add(new System.Windows.Documents.Run("   " + string.Join("  ·  ", facts)) { Foreground = B("Muted"), FontSize = 12 });
                text.Children.Add(title);
                var (verdict, colour) = Verdict(l);
                var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
                line.Inlines.Add(new System.Windows.Documents.Run(verdict) { Foreground = B(colour), FontWeight = FontWeights.SemiBold });
                if (!string.IsNullOrEmpty(l.Reason)) line.Inlines.Add(new System.Windows.Documents.Run("   " + l.Reason) { Foreground = B("Muted") });
                text.Children.Add(line);
                if (l.Fix == "strangers")
                {
                    var fix = Btn(T("fix_strangers"), false, () =>
                    {
                        if (Ask(T("strangers_ask"), T("strangers_on"))) Change(() => Hf.Run(install, "settings", "--strangers", "yes"));
                    });
                    fix.HorizontalAlignment = HorizontalAlignment.Left;
                    fix.Margin = new Thickness(-8, 6, 0, 0);
                    text.Children.Add(fix);
                }
                else if (l.Fix == "editedStats") text.Children.Add(Small(T("fix_edited_stats"), new Thickness(0, 3, 0, 0)));
                var mods = l.Mods.Where(m => m.Tier == "shared" && m.Action != null && m.Action != "have").ToList();
                foreach (var m in mods.Take(6))
                    text.Children.Add(Small((m.Name ?? m.Id) + (string.IsNullOrEmpty(m.Version) ? "" : " " + m.Version) + ":  " + ActionText(m), new Thickness(0, 2, 0, 0)));
                if (mods.Count > 6) text.Children.Add(Small(T("lobby_more_mods", mods.Count - 6), new Thickness(0, 2, 0, 0)));
                g.Children.Add(text);
                return new Border { Child = g, BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 2, 0, 6) };
            }

            (string, string) Verdict(Lobbies.Lobby l)
            {
                switch (l.Verdict)
                {
                    case "ready": return (T("verdict_ready"), "Blue");
                    case "download": return (T("verdict_download", Lobbies.Size(l.DownloadBytes)), "Blue");
                    case "trust": return (T("verdict_trust"), "Yellow");
                    case "full": return (T("verdict_full"), "Muted");
                    case "locked": return (T("verdict_locked"), "Muted");
                    case "blocked": return (T("verdict_blocked"), "Muted");
                    case "unknown": return (T("verdict_unknown"), "Muted");
                    case "unjoinable": return (T("verdict_unjoinable"), "Red");
                    default: return (l.Verdict ?? "", "Muted");
                }
            }

            string ActionText(Lobbies.Mod m)
            {
                var t = T("action_" + (m.Action ?? ""));
                if (t.StartsWith("action_")) t = m.Action;
                return string.IsNullOrEmpty(m.Why) ? t : t + " (" + m.Why + ")";
            }

            async void Join(Lobbies.Lobby l)
            {
                var backend = Lobbies.BackendUrl(RunNow);
                if (backend == null) return;
                var a = await Task.Run(() => Lobbies.Join(backend, l.Id));
                Invited(a, l.HostName);
            }

            async void QuickJoin()
            {
                var backend = Lobbies.BackendUrl(RunNow);
                if (backend == null) return;
                var a = await Task.Run(() => Lobbies.Quick(backend));
                Invited(a, Json.Str(a.Json, "hostName"));
            }

            void Invited(Lobbies.Answer a, string host)
            {
                if (a.Code == 202)
                {
                    lobbyNote = T("invited", host ?? T("unknown_host"));
                    Foot(lobbyNote);
                    lobbySignature = null;
                    Render();
                    return;
                }
                switch (a.Error)
                {
                    case "HELLFORGE_LOBBY_UNKNOWN": Tell(T("lobby_gone")); break;
                    case "HELLFORGE_NO_JOINABLE_LOBBY": Tell(T("no_joinable")); break;
                    case "HELLFORGE_MULTIPLAYER_OFF": Tell(T("multiplayer_off")); break;
                    default: Tell(T("refused", a.Message ?? a.Error ?? ("HTTP " + a.Code))); break;
                }
                lobbySignature = null;
                RefreshLobbies();
            }

            static string WebLink(string url) => Lobbies.WebLink(url);

            void OpenWeb(string url)
            {
                if (WebLink(url) == null) return;
                if (!Ask(T("open_page_ask", url), T("open_page"))) return;
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException) { Tell(e.Message); }
            }

            // ---------------------------------------------------------------- logs

            void RenderLogs()
            {
                var row = Row();
                row.Children.Add(Btn(T("open_bingus_log"), false, () => OpenFile(Install.BingusLog(install, status.Running ? status.LastRun : null))));
                row.Children.Add(Btn(T("open_logs_folder"), false, () => OpenFile(Path.Combine(install, "logs"))));
                body.Children.Add(row);
                if (runs.Count == 0) { body.Children.Add(Para(T("logs_none"))); return; }
                body.Children.Add(Small(T("logs_hint") + (runs.Any(r => EndingOf(r) == Hf.Ending.EndedWithError) ? " " + T("ended_error_hint") : ""), new Thickness(0, 8, 0, 8)));
                foreach (var r in runs)
                {
                    var g = new DockPanel { MinHeight = 48 };
                    var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    buttons.Children.Add(Btn(T("log_open"), false, () => OpenFile(Path.Combine(r.Folder, "session.log"))));
                    buttons.Children.Add(Btn(T("log_folder"), false, () => OpenFile(r.Folder)));
                    buttons.Children.Add(Btn(T("log_export"), false, () => Export(r, false)));
                    var ending = EndingOf(r);
                    if (ending == Hf.Ending.Crashed || ending == Hf.Ending.EndedWithError || ending == Hf.Ending.Failed)
                        buttons.Children.Add(Btn(T("log_crash_report"), false, () => Export(r, true)));
                    DockPanel.SetDock(buttons, Dock.Right);
                    g.Children.Add(buttons);
                    var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    text.Children.Add(new TextBlock { Text = When(r.Started) + "   " + Ending(r), FontSize = 14, Foreground = ending == Hf.Ending.Crashed || ending == Hf.Ending.Failed ? B("Red") : B("Ink") });
                    if (!string.IsNullOrEmpty(r.Failure)) text.Children.Add(Small(r.Failure, new Thickness(0, 2, 0, 0)));
                    g.Children.Add(text);
                    body.Children.Add(new Border { Child = g, BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4, 0, 4) });
                }
            }

            readonly Dictionary<string, Hf.Ending> endings = new Dictionary<string, Hf.Ending>();

            Hf.Ending EndingOf(Hf.Session r)
            {
                if (r.Id != null && endings.TryGetValue(r.Id, out var known) && known != Hf.Ending.Running) return known;
                var e = Hf.EndingOf(r, r.Folder != null ? Hf.ResultOf(r.Folder) : null);
                if (r.Id != null) endings[r.Id] = e;
                return e;
            }

            string Ending(Hf.Session r)
            {
                switch (EndingOf(r))
                {
                    case Hf.Ending.Running: return T("ending_running");
                    case Hf.Ending.Crashed: return T("ending_crashed");
                    case Hf.Ending.EndedWithError: return T("ending_error");
                    case Hf.Ending.Failed: return T("ending_failed");
                    default: return T("ending_ended");
                }
            }

            static string When(string utc) =>
                DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                    ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : utc ?? "";

            async void Export(Hf.Session r, bool crash)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Zip (*.zip)|*.zip", OverwritePrompt = true, AddExtension = true, DefaultExt = ".zip",
                    FileName = (crash ? "HellForge crash report " : "HellForge logs ") + (r.Id ?? "").Substring(0, Math.Min(8, (r.Id ?? "").Length)) + ".zip",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                };
                if (dlg.ShowDialog() != true) return;
                var file = dlg.FileName;
                bool dump = crash && Ask(T("dump_ask"), T("dump_yes"));
                var inst = install;
                Hf.Result result = null;
                await Do(() =>
                {
                    if (File.Exists(file)) File.Delete(file);       // the player agreed to replace it; HellForge wants a new file
                    var args = new List<string> { "logs", "--run", r.Id, crash ? "--crash-report" : "--export", file };
                    if (crash) { args.Add("--dump"); args.Add(dump ? "yes" : "no"); }
                    return result = Hf.Run(inst, args.ToArray());
                }, null);
                if (result != null && result.Ok)
                {
                    Foot(T("exported", file));
                    if (crash) Tell(T("crash_report_done", file, Json.Str(Json.Obj(result.Out), "issuePage") ?? "https://github.com/SkyeShade/HellForge/issues/new"));
                    OpenFile(Path.GetDirectoryName(file));
                }
            }

            // ---------------------------------------------------------------- the session

            void Play()
            {
                if (Locked || install == null) return;
                bool luaOn = mods != null && mods.Mods.Any(m => m.Enabled && m.Has("lua") && !IsBingus(m));
                bool bingusOn = mods != null && mods.Mods.Any(m => m.Enabled && IsBingus(m));
                if (luaOn && !bingusOn && !Ask(T("play_no_bingus_ask"), T("play"))) return;
                var risky = placed.Values.Where(e => mods != null && mods.Mods.Any(m => m.Enabled && string.Equals(m.Id, e.Id, StringComparison.OrdinalIgnoreCase))
                                                    && e.Library != null && Scan.Of(e.Library).Any).Select(e => e.Name).ToList();
                if (risky.Count > 0 && !Ask(T("play_risky_ask", string.Join("\n", risky)), T("play"))) return;
                StartPlay();
            }

            async void StartPlay()
            {
                playStatus = null;
                buildPoll.Tick -= BuildTick;
                buildPoll.Tick += BuildTick;
                buildPoll.Start();
                playing = new CancellationTokenSource();
                stopAsked = false;
                playProblems.Clear();
                playLine = T("play_starting");
                Render();
                var inst = install;
                var token = playing.Token;
                var dispatcher = Root.Dispatcher;
                try
                {
                    await Hf.Streamer(inst, new[] { "play", "--wait", "yes" }, line => dispatcher.BeginInvoke(new Action(() => OnPlayLine(line))), token);
                }
                catch (Exception e) when (e is IOException || e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
                {
                    playLine = T("refused", e.Message);
                }
                await Task.Delay(200);           // the last lines go through the dispatcher first
                buildPoll.Stop();
                playing = null;
                stopAsked = false;
                Read();
            }

            void OnPlayLine(string line)
            {
                var d = Json.Obj(line);
                if (d == null) return;
                var ev = Json.Str(d, "event");
                var st = Json.Str(d, "status");
                switch (ev)
                {
                    case "started": playLine = T("play_started"); playRun = Json.Str(d, "run"); break;
                    case "mod-problems":
                        foreach (var p in Json.List(d, "problems"))
                            playProblems.Add(p is string s ? s : p is Dictionary<string, object> o ? (Json.Str(o, "message") ?? Json.Str(o, "why") ?? Json.Write(o)) : Convert.ToString(p));
                        break;
                    case "steam-input": if (Json.Str(d, "note") is string note && note.Length > 0) playProblems.Add(note); break;
                    case "ended": playLine = T("play_ended", StatusText(st ?? Json.Str(d, "result") ?? "")); break;
                    case "error": playLine = T("refused", Json.Str(d, "why") ?? ""); break;
                    default:
                        if (st != null) playLine = StatusText(st) + (Json.Str(d, "failure") is string f && f.Length > 0 ? ": " + f : "");
                        if (st != null) playStatus = st;
                        break;
                }
                RenderHead();
            }

            void BuildTick(object sender, EventArgs e)
            {
                if (playStatus != "preparing" || playRun == null) return;
                Dictionary<string, object> b;
                try
                {
                    var p = System.IO.Path.Combine(playRun, "mod-builds.json");
                    if (!File.Exists(p)) return;
                    b = Json.Obj(File.ReadAllText(p));
                }
                catch (Exception x) when (x is IOException || x is UnauthorizedAccessException) { return; }
                if (b == null) return;
                var stage = Json.Str(b, "stage");
                string line =
                    stage == "tools" ? T("status_preparing_tools")
                    : stage != "done" && Json.Long(b, "total") > 0 && Json.Str(b, "mod") is string mod && mod.Length > 0
                        ? T("status_preparing_mod", mod, Json.Long(b, "done"), Json.Long(b, "total"))
                    : null;
                if (line == null || line == playLine) return;
                playLine = line;
                RenderHead();
            }

            string StatusText(string st)
            {
                var t = T("status_" + st.Replace('-', '_'));
                return t.StartsWith("status_") ? st : t;
            }

            async void Stop()
            {
                var now = stopAsked;
                if (now && !Ask(T("stop_now_ask"), T("stop_now"))) return;
                stopAsked = true;
                playLine = now ? T("stopping_now") : T("stopping");
                RenderHead();
                var inst = install;
                var r = await Task.Run(() => now ? Hf.Run(inst, "stop", "--now", "yes") : Hf.Run(inst, "stop"));
                if (!r.Ok) Tell(T("refused", r.Why));
            }

            // ---------------------------------------------------------------- HellForge.exe, folders

            void OpenLauncher()
            {
                // a session runs (started here, or by the manager): HellForge has its stand-ins for three
                // of the copy's DLLs in place until it ends, and HellForge.exe, which knows only its own
                // sessions, takes the copy for a broken one and shows its setup page (2026-11-12)
                if (status?.Running == true && !Ask(T("launcher_during_session"), T("open_hellforge"))) return;
                try { Process.Start(new ProcessStartInfo(Install.Launcher(install)) { UseShellExecute = true, WorkingDirectory = install }); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException || e is FileNotFoundException) { Tell(e.Message); }
            }

            void SyncAccount()
            {
                if (Proxy.On() && !Ask(T("proxy_ask"), T("account_sync_anyway"))) return;
                Tell(T("account_sync_how"));
                OpenLauncher();
            }

            void ChooseFolder()
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "HellForge (HellForge.exe;hellforge-install.json)|HellForge.exe;hellforge-install.json;bts-install.json", CheckFileExists = true };
                if (dlg.ShowDialog() != true) return;
                var dir = Path.GetDirectoryName(dlg.FileName);
                if (!Install.IsInstall(dir)) { Tell(T("not_an_install")); return; }
                Choose(home, dir);
                Read();
            }

            void OpenFile(string path)
            {
                try
                {
                    if (Directory.Exists(path) || File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    else Tell(T("missing", path));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException) { Tell(e.Message); }
            }

            // ---------------------------------------------------------------- the manager's dialogs and bits

            bool Ask(string text, string action) =>
                host.TryGetValue("ask", out var a) && a is Func<string, string, bool> ask ? ask(text, action)
                : MessageBox.Show(text, Title(lang), MessageBoxButton.OKCancel) == MessageBoxResult.OK;

            void Tell(string text)
            {
                if (host.TryGetValue("tell", out var t) && t is Action<string> tell) tell(text);
                else MessageBox.Show(text, Title(lang));
            }

            void Foot(string text)
            {
                if (host.TryGetValue("foot", out var f) && f is Action<string, string> foot) foot(text, "Blue");
            }

            /// <summary>The manager reads its state again: the rows' notes follow.</summary>
            void Changed()
            {
                notesAt = DateTime.MinValue;
                if (host.TryGetValue("refresh", out var r) && r is Action refresh) refresh();
            }

            Button Btn(string text, bool primary, Action click)
            {
                var b = new Button { Content = text, Style = S(primary ? "Primary" : "Ghost"), MinHeight = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0) };
                b.Click += (s, e) => click();
                return b;
            }

            Button Glyph(string glyph, string tip, bool enabled, Action click)
            {
                var b = new Button
                {
                    Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
                    Style = S("Ghost"), Width = 34, MinHeight = 30, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip, IsEnabled = enabled,
                };
                b.Click += (s, e) => click();
                return b;
            }

            StackPanel Row() => new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 8, 0, 8) };

            TextBlock Small(string text, Thickness margin) =>
                new TextBlock { Text = text, FontSize = 12, LineHeight = 16, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = margin };

            TextBlock Para(string text) => new TextBlock { Text = text, FontSize = 14, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

            TextBlock Line(string text, string colour) =>
                new TextBlock { Text = text, Foreground = B(colour), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        }
    }
}
