using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using PadDeck.Core;
using Glyphs = PadDeck.Core.Glyphs;
using WF = System.Windows.Forms;

namespace PadDeck.Ui
{
    public sealed class MainWindow : Window
    {
        readonly AppSettings cfg = AppSettings.Load();
        readonly DispatcherTimer saveTimer, statusTimer;
        readonly Border[] tiles = new Border[AppSettings.ButtonCount];
        readonly TextBlock[] tileGlyphs = new TextBlock[AppSettings.ButtonCount];
        readonly TextBlock[] tileLabels = new TextBlock[AppSettings.ButtonCount];
        readonly bool preview;

        PopupWindow popup;
        KeyHook hook;
        WindowPlacer placer;
        StackPanel windowRows;
        WF.NotifyIcon tray;
        WF.ToolStripMenuItem trayPopups;
        Border editorHost, banner;
        TextBlock statusText, testResult;
        Ellipse statusDot;
        CheckBox popupSw;
        int sel;
        bool quitting, recording;

        ButtonConfig Cur => cfg.Buttons[sel];
        static string KeyOf(int i) => "F" + (19 + i);

        public MainWindow(bool startHidden, bool preview = false)
        {
            this.preview = preview;
            Title = "PadDeck";
            Width = 1060; Height = 640; MinWidth = 960; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Bg;
            Foreground = Theme.Text;
            FontFamily = Theme.Ui;
            FontSize = 13;
            UseLayoutRounding = true;
            Content = Build();
            Select(0);

            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); cfg.Save(); };
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            statusTimer.Tick += (s, e) => UpdateStatus();

            SourceInitialized += (s, e) => DarkTitleBar();
            IsVisibleChanged += (s, e) => { if (IsVisible) { UpdateStatus(); statusTimer.Start(); } else statusTimer.Stop(); };
            Closing += OnClosing;
            if (preview) { SetStatus("Pad connected", Theme.Accent); return; }

            if (!cfg.FirstRunDone)
            {
                cfg.FirstRunDone = true;
                try { Autostart.Enabled = true; } catch { }
                cfg.Save();
            }
            popup = new PopupWindow();
            hook = new KeyHook { OnPadKey = OnPadKey };
            placer = new WindowPlacer(() => cfg.Windows, Dispatcher);
            SetupTray();
            UpdateStatus();
            if (!startHidden) Show();
        }

        /// <summary>Lays the window out offscreen with sample settings and saves it as a PNG.</summary>
        public void RenderSnapshot(string path, int w, int h)
        {
            var content = (FrameworkElement)Content;
            Content = null;
            var host = new Border { Background = Background, Child = content, Width = w, Height = h };
            TextElement.SetFontFamily(host, FontFamily);
            TextElement.SetFontSize(host, FontSize);
            TextElement.SetForeground(host, Foreground);
            Snapshot.Save(host, w, h, path);
        }

        public void SelectButton(int i) { UpdateStatus(); Select(i); }

        /// <summary>Fills in example buttons for snapshots (not saved).</summary>
        public void LoadSample(int select = 3)
        {
            cfg.Buttons[0] = new ButtonConfig { Kind = ActionKind.MicMute };
            cfg.Buttons[1] = new ButtonConfig { Kind = ActionKind.OutputDevice, Label = "KA13 / Speakers", Devices = { new DeviceRef { Id = "x", Name = "Headphones (FIIO KA13)" } } };
            cfg.Buttons[2] = new ButtonConfig { Kind = ActionKind.Open, Target = @"C:\Program Files\Discord\Discord.exe", Label = "Discord" };
            cfg.Buttons[3] = new ButtonConfig { Kind = ActionKind.Keys, Keys = "Ctrl+Shift+M", Label = "Discord mute" };
            cfg.Buttons[4] = new ButtonConfig { Kind = ActionKind.Text, Text = "gg wp\n" };
            cfg.Buttons[5] = new ButtonConfig();
            cfg.PadProgrammed = true;
            for (int i = 0; i < tiles.Length; i++) RefreshTile(i);
            UpdateStatus();
            Select(select);
        }

        // ---------------- layout ----------------

        UIElement Build()
        {
            var root = new DockPanel();
            var top = TopBar();
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            var foot = Footer();
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            var grid = new Grid { Margin = new Thickness(16, 16, 16, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var padCard = PadCard();
            grid.Children.Add(padCard);
            editorHost = new Border();
            var editorCard = Card(editorHost);
            editorCard.Margin = new Thickness(14, 0, 0, 0);
            Grid.SetColumn(editorCard, 1);
            grid.Children.Add(editorCard);
            root.Children.Add(grid);
            return root;
        }

        static Border Card(UIElement child) => new Border
        {
            Background = Theme.Panel,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 16, 18, 16),
            Child = child,
        };

        FrameworkElement TopBar()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = "PadDeck", FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            statusDot = new Ellipse { Width = 8, Height = 8, Fill = Theme.Muted, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            statusText = new TextBlock { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(statusDot);
            status.Children.Add(statusText);
            Grid.SetColumn(status, 2);
            g.Children.Add(status);

            return new Border
            {
                Background = Theme.Panel,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(18, 12, 18, 12),
                Child = g,
            };
        }

        FrameworkElement PadCard()
        {
            var sp = new StackPanel { Width = 520 };
            sp.Children.Add(new TextBlock { Text = "Your pad", FontSize = 14, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = "Click a button to edit it, or press it on the pad.", FontSize = 12, Foreground = Theme.Muted, Margin = new Thickness(0, 2, 0, 16) });

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            var keys = new UniformGrid3x2();
            for (int i = 0; i < tiles.Length; i++) keys.Children.Add(Tile(i));
            body.Children.Add(keys);

            var knob = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 4, 0, 0) };
            var dial = new Grid { Width = 76, Height = 76 };
            dial.Children.Add(new Ellipse { Fill = Theme.Panel2, Stroke = Theme.Line, StrokeThickness = 1 });
            dial.Children.Add(new Ellipse { Width = 56, Height = 56, Fill = Theme.Track });
            dial.Children.Add(new Rectangle { Width = 3, Height = 14, RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.Muted, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 14, 0, 0) });
            knob.Children.Add(dial);
            knob.Children.Add(new TextBlock { Text = "Knob", FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2) });
            knob.Children.Add(new TextBlock { Text = "Turn: volume\nPress: play/pause", FontSize = 11, Foreground = Theme.Muted, TextAlignment = TextAlignment.Center });
            Grid.SetColumn(knob, 1);
            body.Children.Add(knob);
            sp.Children.Add(body);

            banner = new Border
            {
                Background = Theme.Tint(Theme.Amber, 0.12),
                BorderBrush = Theme.Tint(Theme.Amber, 0.4),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 18, 0, 0),
            };
            var bp = new DockPanel();
            var prog = new Button { Content = "Program pad", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            prog.Click += (s, e) => ProgramPad();
            DockPanel.SetDock(prog, Dock.Right);
            bp.Children.Add(prog);
            bp.Children.Add(new TextBlock { Text = "The pad still sends F13–F18. Program it once so its buttons send F19–F24, which only PadDeck listens to.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Theme.Text });
            banner.Child = bp;
            sp.Children.Add(banner);
            sp.Children.Add(WindowsSection());
            return Card(sp);
        }

        // ---------------- window placement ----------------

        static readonly AudioDevice[] MonitorItems =
        {
            new AudioDevice(nameof(MonitorPick.Left), "Left monitor"),
            new AudioDevice(nameof(MonitorPick.Right), "Right monitor"),
            new AudioDevice(nameof(MonitorPick.Main), "Main monitor"),
        };

        static readonly AudioDevice[] ZoneItems =
        {
            new AudioDevice(nameof(Zone.LeftHalf), "Left half"),
            new AudioDevice(nameof(Zone.RightHalf), "Right half"),
            new AudioDevice(nameof(Zone.TopHalf), "Top half"),
            new AudioDevice(nameof(Zone.BottomHalf), "Bottom half"),
            new AudioDevice(nameof(Zone.Full), "Whole screen"),
            new AudioDevice(nameof(Zone.Maximized), "Maximized"),
        };

        FrameworkElement WindowsSection()
        {
            var sp = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
            sp.Children.Add(new TextBlock { Text = "Window placement", FontSize = 14, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = "These apps’ windows go to the same spot every time they open.", FontSize = 12, Foreground = Theme.Muted, Margin = new Thickness(0, 2, 0, 12) });
            windowRows = new StackPanel();
            sp.Children.Add(windowRows);
            BuildWindowRows();

            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var add = new ComboBox { Width = 230, Height = 32 };
            add.Items.Add(new AudioDevice(null, "Add an open app…"));
            add.SelectedIndex = 0;
            add.DropDownOpened += (s, e) =>
            {
                var have = new HashSet<string>(cfg.Windows.Select(x => x.Process), StringComparer.OrdinalIgnoreCase);
                var apps = Reset.Windows().Select(w => w.Process).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => !have.Contains(n) && n != "PadDeck" && n != "explorer").OrderBy(n => n).ToList();
                add.Items.Clear();
                add.Items.Add(new AudioDevice(null, apps.Count == 0 ? "No other apps are open" : "Add an open app…"));
                foreach (var n in apps) add.Items.Add(new AudioDevice(n, n));
                add.SelectedIndex = 0;
            };
            add.SelectionChanged += (s, e) =>
            {
                if (add.SelectedItem is not AudioDevice { Id: not null } d) return;
                cfg.Windows.Add(new WindowRule { Name = AppTitle(d.Id), Process = d.Id, Monitor = MonitorPick.Main, Zone = Zone.Maximized });
                MarkDirty();
                Dispatcher.BeginInvoke(new Action(() => { BuildWindowRows(); add.SelectedIndex = 0; }));
            };
            addRow.Children.Add(add);
            var apply = new Button { Content = "Apply now", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Move the open windows into place right away" };
            apply.Click += (s, e) => placer?.ApplyAll();
            addRow.Children.Add(apply);
            sp.Children.Add(addRow);
            return sp;
        }

        static string AppTitle(string process)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(process))
            {
                using (p)
                {
                    try
                    {
                        var desc = p.MainModule.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
                    }
                    catch { }
                }
            }
            return process;
        }

        void BuildWindowRows()
        {
            windowRows.Children.Clear();
            foreach (var r in cfg.Windows.ToList())
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                name.Inlines.Add(new Run(r.Name) { FontWeight = FontWeights.SemiBold });
                name.Inlines.Add(new Run("  " + r.Process) { FontFamily = Theme.Mono, FontSize = 10.5, Foreground = Theme.Muted });
                g.Children.Add(name);

                var mon = new ComboBox { Height = 30, Margin = new Thickness(6, 0, 0, 0), ItemsSource = MonitorItems };
                mon.SelectedItem = MonitorItems.First(x => x.Id == r.Monitor.ToString());
                mon.SelectionChanged += (s, e) => { r.Monitor = Enum.Parse<MonitorPick>(((AudioDevice)mon.SelectedItem).Id); MarkDirty(); };
                Grid.SetColumn(mon, 1);
                g.Children.Add(mon);

                var zone = new ComboBox { Height = 30, Margin = new Thickness(6, 0, 0, 0), ItemsSource = ZoneItems };
                zone.SelectedItem = ZoneItems.First(x => x.Id == r.Zone.ToString());
                zone.SelectionChanged += (s, e) => { r.Zone = Enum.Parse<Zone>(((AudioDevice)zone.SelectedItem).Id); MarkDirty(); };
                Grid.SetColumn(zone, 2);
                g.Children.Add(zone);

                var del = new Button { Content = new TextBlock { Text = "", FontFamily = Theme.Icons, FontSize = 10 }, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Stop placing this app" };
                del.Click += (s, e) => { cfg.Windows.Remove(r); MarkDirty(); BuildWindowRows(); };
                Grid.SetColumn(del, 3);
                g.Children.Add(del);
                windowRows.Children.Add(g);
            }
        }

        /// <summary>3 columns × 2 rows of fixed-size tiles.</summary>
        sealed class UniformGrid3x2 : System.Windows.Controls.Primitives.UniformGrid
        {
            public UniformGrid3x2() { Columns = 3; Rows = 2; }
        }

        Border Tile(int i)
        {
            var g = new Grid();
            tileGlyphs[i] = new TextBlock { FontFamily = Theme.Icons, FontSize = 22, Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            g.Children.Add(tileGlyphs[i]);
            g.Children.Add(new TextBlock { Text = KeyOf(i), FontFamily = Theme.Mono, FontSize = 10, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
            tileLabels[i] = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36, VerticalAlignment = VerticalAlignment.Bottom };
            g.Children.Add(tileLabels[i]);

            var b = new Border
            {
                Width = 122, Height = 104, Margin = new Thickness(0, 0, 10, 10),
                CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5),
                Background = Theme.Panel2, BorderBrush = Theme.Line,
                Padding = new Thickness(12, 10, 10, 10), Cursor = Cursors.Hand, Child = g,
            };
            b.MouseLeftButtonUp += (s, e) => Select(i);
            b.MouseEnter += (s, e) => { if (sel != i) b.BorderBrush = Theme.Track; };
            b.MouseLeave += (s, e) => { if (sel != i) b.BorderBrush = Theme.Line; };
            tiles[i] = b;
            RefreshTile(i);
            return b;
        }

        void RefreshTile(int i)
        {
            var b = cfg.Buttons[i];
            tileGlyphs[i].Text = Glyphs.For(b.Kind);
            tileGlyphs[i].Foreground = b.IsSet ? Theme.Accent : Theme.Track;
            tileLabels[i].Text = b.IsSet ? ActionRunner.Title(b) : "Not set";
            tileLabels[i].Foreground = b.IsSet ? Theme.Text : Theme.Muted;
            tileLabels[i].FontWeight = b.IsSet ? FontWeights.SemiBold : FontWeights.Normal;
        }

        void Flash(int i)
        {
            var brush = new SolidColorBrush(((SolidColorBrush)Theme.Accent).Color) { Opacity = 0.35 };
            tiles[i].Background = brush;
            var a = new DoubleAnimation(0.35, 0, TimeSpan.FromMilliseconds(450));
            a.Completed += (s, e) => tiles[i].Background = Theme.Panel2;
            brush.BeginAnimation(Brush.OpacityProperty, a);
        }

        FrameworkElement Footer()
        {
            var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            CheckBox Sw(string text, bool on, string tip, Action<bool> set)
            {
                var c = new CheckBox { Style = Theme.Switch, Content = text, IsChecked = on, Margin = new Thickness(0, 4, 22, 4), ToolTip = tip };
                c.Click += (s, e) => { set(c.IsChecked == true); MarkDirty(); };
                wrap.Children.Add(c);
                return c;
            }
            popupSw = Sw("Popups", cfg.PopupOn, "Show what a button did, without taking focus", v => { cfg.PopupOn = v; if (trayPopups != null) trayPopups.Checked = v; });
            var durations = new[] { 2, 3, 4, 5, 8 }.Select(n => new AudioDevice(n.ToString(), n + " s")).ToList();
            var durBox = new ComboBox { Width = 72, Height = 30, ItemsSource = durations, Margin = new Thickness(-10, 4, 22, 4), ToolTip = "How long popups stay on screen" };
            durBox.SelectedItem = durations.OrderBy(d => Math.Abs(int.Parse(d.Id) - cfg.PopupSeconds)).First();
            durBox.SelectionChanged += (s, e) => { cfg.PopupSeconds = int.Parse(((AudioDevice)durBox.SelectedItem).Id); MarkDirty(); };
            wrap.Children.Add(durBox);
            Sw("At top", cfg.PopupTop, "Show popups at the top of the screen instead of the bottom", v => cfg.PopupTop = v);
            Sw("Hide over fullscreen games", cfg.PopupSkipFullscreen, "Skip the popup when a fullscreen game or app is in front", v => cfg.PopupSkipFullscreen = v);

            var screens = WF.Screen.AllScreens.Select((s, n) => new AudioDevice(s.DeviceName,
                $"Display {n + 1} · {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (main)" : "")}")).ToList();
            var screenBox = new ComboBox { Width = 215, Height = 30, ItemsSource = screens, Margin = new Thickness(0, 4, 22, 4), ToolTip = "Which monitor shows popups" };
            screenBox.SelectedItem = screens.FirstOrDefault(s => s.Id == cfg.PopupScreen) ?? screens.FirstOrDefault(s => s.Name.EndsWith("(main)"));
            screenBox.SelectionChanged += (s, e) => { cfg.PopupScreen = (screenBox.SelectedItem as AudioDevice)?.Id; MarkDirty(); };
            wrap.Children.Add(screenBox);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var auto = new CheckBox { Style = Theme.Switch, Content = "Start with Windows", IsChecked = !preview && Autostart.Enabled, Margin = new Thickness(0, 0, 22, 0), ToolTip = "Starts hidden in the tray at sign-in" };
            if (preview) auto.IsChecked = true;
            auto.Click += (s, e) => { try { Autostart.Enabled = auto.IsChecked == true; } catch { auto.IsChecked = Autostart.Enabled; } };
            right.Children.Add(auto);
            var prog = new Button { Content = "Program pad…", ToolTip = "Write F19–F24 to the buttons and volume / play-pause to the knob" };
            prog.Click += (s, e) => ProgramPad();
            right.Children.Add(prog);

            var dp = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            dp.Children.Add(right);
            dp.Children.Add(wrap);
            return new Border { Padding = new Thickness(18, 8, 18, 12), Child = dp };
        }

        // ---------------- editor ----------------

        void Select(int i)
        {
            if (recording) recording = false;
            int prev = sel;
            sel = i;
            tiles[prev].BorderBrush = Theme.Line;
            tiles[i].BorderBrush = Theme.Accent;
            editorHost.Child = Editor();
        }

        UIElement Editor()
        {
            var b = Cur;
            var sp = new StackPanel();
            var head = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
            head.Inlines.Add(new Run("Button " + (sel + 1) + "  "));
            head.Inlines.Add(new Run(KeyOf(sel)) { Foreground = Theme.Muted, FontFamily = Theme.Mono, FontSize = 12 });
            sp.Children.Add(head);

            sp.Children.Add(Caption("Action", 14));
            var kinds = new WrapPanel();
            string group = "kind" + sel;
            foreach (ActionKind k in Enum.GetValues(typeof(ActionKind)))
            {
                var rb = new RadioButton { Style = Theme.Seg, GroupName = group, IsChecked = b.Kind == k, Margin = new Thickness(0, 0, 0, 6) };
                var c = new StackPanel { Orientation = Orientation.Horizontal };
                if (k != ActionKind.None) c.Children.Add(new TextBlock { Text = Glyphs.For(k), FontFamily = Theme.Icons, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                c.Children.Add(new TextBlock { Text = ActionRunner.Describe(k) });
                rb.Content = c;
                rb.Checked += (s, e) =>
                {
                    if (Cur.Kind == k) return;
                    Cur.Kind = k;
                    if (k == ActionKind.Reset && Cur.Core.Count == 0) Cur.Core = Reset.DefaultCore();
                    Changed();
                    editorHost.Child = Editor();
                };
                kinds.Children.Add(rb);
            }
            sp.Children.Add(kinds);

            if (b.Kind == ActionKind.None)
            {
                sp.Children.Add(Note("Pick what this button should do. While it’s set to Nothing, the key passes through to other apps, so you can bind " + KeyOf(sel) + " in a game or in OBS directly.", 12));
                return sp;
            }

            sp.Children.Add(Caption("Name on the popup", 14));
            var label = new TextBox { Text = b.Label };
            var labelHint = Hint(label, ActionRunner.AutoLabel(b));
            label.TextChanged += (s, e) => { Cur.Label = label.Text; Changed(); };
            sp.Children.Add(labelHint.host);

            var fields = new StackPanel();
            switch (b.Kind)
            {
                case ActionKind.Open: OpenFields(fields, labelHint.hint); break;
                case ActionKind.Keys: KeysFields(fields, labelHint.hint); break;
                case ActionKind.Text: TextFields(fields, labelHint.hint); break;
                case ActionKind.MicMute: MicFields(fields); break;
                case ActionKind.OutputDevice: OutputFields(fields); break;
                case ActionKind.AppMute: AppFields(fields, labelHint.hint); break;
                case ActionKind.Reset: ResetFields(fields); break;
                case ActionKind.Obs: ObsFields(fields, labelHint.hint); break;
            }
            sp.Children.Add(fields);

            var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
            bool typed = b.Kind == ActionKind.Keys || b.Kind == ActionKind.Text;
            var test = new Button
            {
                Content = typed ? "Test in 3 s" : b.Kind == ActionKind.Reset ? "Run reset" : "Test",
                ToolTip = typed ? "Click, then switch to the window you want the keys to go to"
                    : b.Kind == ActionKind.Reset ? "Click twice: the first click shows what will close" : "Run this action now",
            };
            test.Click += (s, e) => Test(typed);
            testRow.Children.Add(test);
            testResult = new TextBlock { Foreground = Theme.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontSize = 12 };
            testRow.Children.Add(testResult);
            sp.Children.Add(testRow);
            return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        }

        static TextBlock Caption(string t, double top = 12) =>
            new TextBlock { Text = t, FontSize = 12, Foreground = Theme.Muted, Margin = new Thickness(0, top, 0, 6) };

        static TextBlock Note(string t, double top = 6) =>
            new TextBlock { Text = t, FontSize = 11.5, Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };

        /// <summary>A text box with grey placeholder text while it's empty.</summary>
        static (Grid host, TextBlock hint) Hint(TextBox tb, string hint)
        {
            var g = new Grid();
            g.Children.Add(tb);
            var h = new TextBlock { Text = hint, Foreground = Theme.Muted, Opacity = 0.7, IsHitTestVisible = false, Margin = new Thickness(11, 7, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            void Upd() => h.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            tb.TextChanged += (s, e) => Upd();
            Upd();
            g.Children.Add(h);
            return (g, h);
        }

        void OpenFields(StackPanel sp, TextBlock labelHint)
        {
            sp.Children.Add(Caption("File, app, folder or web address"));
            var row = new DockPanel();
            var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse);
            var target = new TextBox { Text = Cur.Target, AllowDrop = true };
            var hint = Hint(target, @"e.g. C:\Games\Apex.lnk or https://twitch.tv");
            row.Children.Add(hint.host);
            sp.Children.Add(row);
            target.TextChanged += (s, e) => { Cur.Target = target.Text; labelHint.Text = ActionRunner.AutoLabel(Cur); Changed(); };
            target.PreviewDragOver += (s, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
            target.Drop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) target.Text = files[0];
            };
            browse.Click += (s, e) =>
            {
                var d = new Microsoft.Win32.OpenFileDialog { Title = "Choose what this button opens", Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd|All files|*.*", DereferenceLinks = false };
                if (d.ShowDialog(this) == true) target.Text = d.FileName;
            };

            sp.Children.Add(Caption("Arguments (optional)"));
            var args = new TextBox { Text = Cur.Args };
            args.TextChanged += (s, e) => { Cur.Args = args.Text; Changed(); };
            sp.Children.Add(args);
            sp.Children.Add(Note("You can drag a shortcut or file onto the box. Steam links like steam://rungameid/1172470 work too."));
        }

        void KeysFields(StackPanel sp, TextBlock labelHint)
        {
            sp.Children.Add(Caption("Keys to press"));
            var row = new DockPanel();
            var rec = new Button { Content = "Record", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Press the combo you want on your keyboard" };
            DockPanel.SetDock(rec, Dock.Right);
            row.Children.Add(rec);
            var keys = new TextBox { Text = Cur.Keys, FontFamily = Theme.Mono };
            row.Children.Add(Hint(keys, "e.g. Ctrl+Shift+M").host);
            sp.Children.Add(row);
            var check = Note("");
            sp.Children.Add(check);
            sp.Children.Add(Note("Separate combos with commas to press them in order, e.g. Ctrl+C, Ctrl+V. Media keys: PlayPause, NextTrack, PrevTrack, Mute.", 4));

            void Validate()
            {
                try
                {
                    var seq = KeyCombo.Parse(keys.Text);
                    check.Text = seq.Count == 0 ? "" : "Will press: " + KeyCombo.Format(seq);
                    check.Foreground = Theme.Muted;
                }
                catch (FormatException ex) { check.Text = ex.Message; check.Foreground = Theme.Amber; }
            }
            keys.TextChanged += (s, e) => { if (recording) return; Cur.Keys = keys.Text; labelHint.Text = ActionRunner.AutoLabel(Cur); Validate(); Changed(); };
            Validate();

            rec.Click += (s, e) =>
            {
                recording = true;
                rec.Content = "Press keys…";
                keys.Focus();
                keys.SelectAll();
            };
            keys.PreviewKeyDown += (s, e) =>
            {
                if (!recording) return;
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape) { recording = false; rec.Content = "Record"; return; }
                ushort vk = KeyCombo.NormalizeVk(KeyInterop.VirtualKeyFromKey(key));
                if (vk == 0 || KeyCombo.IsModifier(vk)) return; // wait for the main key
                var combo = new List<ushort>();
                var m = Keyboard.Modifiers;
                if (m.HasFlag(ModifierKeys.Windows)) combo.Add(KeyCombo.Win);
                if (m.HasFlag(ModifierKeys.Control)) combo.Add(KeyCombo.Ctrl);
                if (m.HasFlag(ModifierKeys.Shift)) combo.Add(KeyCombo.Shift);
                if (m.HasFlag(ModifierKeys.Alt)) combo.Add(KeyCombo.Alt);
                combo.Add(vk);
                recording = false;
                rec.Content = "Record";
                keys.Text = KeyCombo.Format(combo);
                keys.CaretIndex = keys.Text.Length;
            };
            keys.LostKeyboardFocus += (s, e) => { recording = false; rec.Content = "Record"; };
        }

        void TextFields(StackPanel sp, TextBlock labelHint)
        {
            sp.Children.Add(Caption("Text to type"));
            var text = new TextBox { Text = Cur.Text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, Height = 120, VerticalContentAlignment = VerticalAlignment.Top };
            text.TextChanged += (s, e) => { Cur.Text = text.Text; labelHint.Text = ActionRunner.AutoLabel(Cur); Changed(); };
            sp.Children.Add(text);
            sp.Children.Add(Note("It’s typed into whatever window is in front. A new line presses Enter, so end with one to send a chat message."));
        }

        void MicFields(StackPanel sp)
        {
            sp.Children.Add(Caption("Microphone"));
            var items = new List<AudioDevice> { new AudioDevice(null, "Default mic") };
            try { items.AddRange(AudioCtl.Devices(DataFlow.Capture)); } catch { }
            var box = new ComboBox { Height = 32, ItemsSource = items };
            box.SelectedItem = items.FirstOrDefault(d => d.Id == Cur.DeviceId) ?? items[0];
            box.SelectionChanged += (s, e) => { Cur.DeviceId = (box.SelectedItem as AudioDevice)?.Id; Changed(); };
            sp.Children.Add(box);
            sp.Children.Add(Note("Mutes the mic in Windows, so every app (Discord, OBS, games) hears silence. The popup turns red while it’s muted."));
        }

        void OutputFields(StackPanel sp)
        {
            sp.Children.Add(Caption("Devices"));
            var flow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (bool rec in new[] { false, true })
            {
                var rb = new RadioButton { Style = Theme.Seg, GroupName = "flow" + sel, Content = rec ? "Recording" : "Playback", IsChecked = Cur.Recording == rec };
                rb.Checked += (s, e) => { if (Cur.Recording == rec) return; Cur.Recording = rec; Cur.Devices.Clear(); Changed(); editorHost.Child = Editor(); };
                flow.Children.Add(rb);
            }
            var import = new Button { Content = "Copy from SoundSwitch", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Use the same device list as SoundSwitch" };
            var ss = AudioCtl.SoundSwitchDevices();
            import.Visibility = ss == null ? Visibility.Collapsed : Visibility.Visible;
            import.Click += (s, e) =>
            {
                Cur.Devices = (Cur.Recording ? ss.Value.recording : ss.Value.playback).ToList();
                Cur.SwitchComms = ss.Value.comms;
                Changed();
                editorHost.Child = Editor();
            };
            flow.Children.Add(import);
            sp.Children.Add(flow);

            List<AudioDevice> active;
            try { active = AudioCtl.Devices(Cur.Recording ? DataFlow.Capture : DataFlow.Render); } catch { active = new List<AudioDevice>(); }
            if (preview && active.Count == 0) active = new List<AudioDevice> { new AudioDevice("a", "DC Headphones (2- FIIO KA13)"), new AudioDevice("b", "Speakers (Realtek(R) Audio)") };

            // Chosen devices first, in cycle order (missing ones greyed out), then the other connected devices.
            var rows = new List<(DeviceRef r, bool on, bool present)>();
            foreach (var r in Cur.Devices)
            {
                var hit = AudioCtl.Resolve(new[] { r }, active).FirstOrDefault();
                if (hit != null && rows.Any(x => x.r.Id == hit.Id)) continue;
                rows.Add((hit != null ? new DeviceRef { Id = hit.Id, Name = hit.Name } : r, true, hit != null));
            }
            foreach (var a in active)
                if (!rows.Any(x => x.r.Id == a.Id)) rows.Add((new DeviceRef { Id = a.Id, Name = a.Name }, false, true));

            var boxes = new List<(CheckBox c, DeviceRef r)>();
            var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var row in rows)
            {
                var content = new TextBlock();
                content.Inlines.Add(new Run(row.r.Name));
                if (!row.present) content.Inlines.Add(new Run("  not connected") { Foreground = Theme.Muted, FontSize = 11.5 });
                var c = new CheckBox { Style = Theme.Switch, Content = content, IsChecked = row.on, Margin = new Thickness(0, 3, 0, 3) };
                c.Click += (s, e) =>
                {
                    Cur.Devices = boxes.Where(x => x.c.IsChecked == true).Select(x => x.r).ToList();
                    Changed();
                };
                boxes.Add((c, row.r));
                list.Children.Add(c);
            }
            sp.Children.Add(list);

            if (!Cur.Recording)
            {
                var comms = new CheckBox { Style = Theme.Switch, Content = "Also switch the communications device", IsChecked = Cur.SwitchComms, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Off: Discord and other call apps keep using their device, like SoundSwitch’s default" };
                comms.Click += (s, e) => { Cur.SwitchComms = comms.IsChecked == true; Changed(); };
                sp.Children.Add(comms);
            }
            sp.Children.Add(Note("Each press makes the next switched-on device the default, skipping any that are unplugged. Devices that come back under a new name, like “2- FIIO KA13”, are still recognised."));
        }

        void AppFields(StackPanel sp, TextBlock labelHint)
        {
            sp.Children.Add(Caption("App"));
            var row = new DockPanel();
            var pick = new ComboBox { Width = 190, Height = 32, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Apps playing audio right now" };
            pick.DropDownOpened += (s, e) => { try { pick.ItemsSource = AudioCtl.AudioApps().Select(n => new AudioDevice(n, n)).ToList(); } catch { } };
            DockPanel.SetDock(pick, Dock.Right);
            row.Children.Add(pick);
            var app = new TextBox { Text = Cur.App };
            row.Children.Add(Hint(app, "e.g. Discord").host);
            sp.Children.Add(row);
            app.TextChanged += (s, e) => { Cur.App = app.Text; labelHint.Text = ActionRunner.AutoLabel(Cur); Changed(); };
            pick.SelectionChanged += (s, e) => { if (pick.SelectedItem is AudioDevice d) app.Text = d.Name; };
            sp.Children.Add(Note("Mutes just that app’s sound in the Windows volume mixer. Press again to unmute. Pick from the list while the app is playing something."));
        }

        void ObsFields(StackPanel sp, TextBlock labelHint)
        {
            sp.Children.Add(Caption("Command"));
            var row = new WrapPanel();
            foreach (ObsCommand c in Enum.GetValues(typeof(ObsCommand)))
            {
                var rb = new RadioButton { Style = Theme.Seg, GroupName = "obs" + sel, Content = Obs.Describe(c), IsChecked = Cur.ObsAction == c, Margin = new Thickness(0, 0, 0, 6) };
                rb.Checked += (s, e) => { Cur.ObsAction = c; labelHint.Text = ActionRunner.AutoLabel(Cur); Changed(); };
                row.Children.Add(rb);
            }
            sp.Children.Add(row);
            sp.Children.Add(Note("Talks to OBS directly over its built-in WebSocket server, so no keys are sent to your game. The popup shows the saved file, or tells you if the replay buffer is off. Uses the port and password from OBS’s own settings."));
        }

        void ResetFields(StackPanel sp)
        {
            sp.Children.Add(Caption("Core apps: kept open, and started if they aren’t running"));
            var rows = new StackPanel();
            foreach (var c in Cur.Core.ToList())
            {
                // Two lines per app: name and remove button, then the full-width start command.
                var head = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var del = new Button { Content = new TextBlock { Text = "", FontFamily = Theme.Icons, FontSize = 10 }, Padding = new Thickness(8, 5, 8, 5), ToolTip = "Remove from core apps" };
                del.Click += (s, e) => { Cur.Core.Remove(c); Changed(); editorHost.Child = Editor(); };
                DockPanel.SetDock(del, Dock.Right);
                head.Children.Add(del);
                var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                name.Inlines.Add(new Run(c.Name) { FontWeight = FontWeights.SemiBold });
                name.Inlines.Add(new Run("   " + c.Process) { FontFamily = Theme.Mono, FontSize = 10.5, Foreground = Theme.Muted });
                head.Children.Add(name);

                var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                var target = new TextBox { Text = c.Target, FontSize = 12, ToolTip = "What to run when it isn’t running. Leave empty to only keep it open." };
                target.TextChanged += (s, e) => { c.Target = target.Text; MarkDirty(); };
                var th = Hint(target, "nothing (only keep it open)").host;
                th.Margin = new Thickness(0, 0, 6, 0);
                g.Children.Add(th);
                var args = new TextBox { Text = c.Args, FontSize = 12, ToolTip = "Arguments" };
                args.TextChanged += (s, e) => { c.Args = args.Text; MarkDirty(); };
                var ah = Hint(args, "arguments").host;
                Grid.SetColumn(ah, 1);
                g.Children.Add(ah);

                rows.Children.Add(head);
                rows.Children.Add(g);
            }
            sp.Children.Add(rows);

            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var add = new ComboBox { Width = 260, Height = 32, ToolTip = "Make an app that’s open right now a core app" };
            add.Items.Add(new AudioDevice(null, "Add an open app…"));
            add.SelectedIndex = 0;
            add.DropDownOpened += (s, e) =>
            {
                var have = new HashSet<string>(Cur.Core.Select(x => x.Process), StringComparer.OrdinalIgnoreCase);
                var apps = Reset.Windows().Select(w => w.Process).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => !Reset.AlwaysKeep.Contains(n) && !have.Contains(n)).OrderBy(n => n).ToList();
                add.Items.Clear();
                add.Items.Add(new AudioDevice(null, apps.Count == 0 ? "No other apps are open" : "Add an open app…"));
                foreach (var n in apps) add.Items.Add(new AudioDevice(n, n));
                add.SelectedIndex = 0;
            };
            add.SelectionChanged += (s, e) =>
            {
                if (add.SelectedItem is not AudioDevice { Id: not null } d) return;
                string path = "", title = d.Name;
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(d.Id))
                {
                    using (p)
                    {
                        try
                        {
                            path = p.MainModule.FileName;
                            var desc = p.MainModule.FileVersionInfo.FileDescription;
                            if (!string.IsNullOrWhiteSpace(desc)) title = desc.Trim();
                            break;
                        }
                        catch { }
                    }
                }
                Cur.Core.Add(new CoreApp { Name = title, Process = d.Id, Target = path });
                Changed();
                Dispatcher.BeginInvoke(new Action(() => editorHost.Child = Editor()));
            };
            addRow.Children.Add(add);
            var defaults = new Button { Content = "Restore defaults", Margin = new Thickness(8, 0, 0, 0), ToolTip = "OBS, Discord, Fan Control and Thorium" };
            defaults.Click += (s, e) => { Cur.Core = Reset.DefaultCore(); Changed(); editorHost.Child = Editor(); };
            addRow.Children.Add(defaults);
            sp.Children.Add(addRow);

            var gpu = new CheckBox { Style = Theme.Switch, Content = "Stop the local LLM and ComfyUI first (frees the GPU)", IsChecked = Cur.StopGpu, Margin = new Thickness(0, 14, 0, 0) };
            gpu.Click += (s, e) => { Cur.StopGpu = gpu.IsChecked == true; Changed(); };
            sp.Children.Add(gpu);
            sp.Children.Add(Note("Every other app with a window is asked to close, like clicking its X. Apps with unsaved work can still ask, and stay open. " +
                "Apps that only live in the tray are left alone, and Windows Terminal, File Explorer and PadDeck are never closed. " +
                "Press the button twice within 3 seconds: the first press shows what will close.", 10));
        }

        void Changed()
        {
            RefreshTile(sel);
            MarkDirty();
        }

        void MarkDirty()
        {
            if (preview) return;
            saveTimer?.Stop();
            saveTimer?.Start();
        }

        async void Test(bool delayed)
        {
            var b = Cur;
            if (delayed)
            {
                for (int n = 3; n > 0; n--) { testResult.Text = $"Sending in {n}…"; await Task.Delay(1000); }
            }
            testResult.Text = "";
            Fire(b);
        }

        ButtonConfig armed;
        DateTime armedAt;

        /// <summary>Runs a button's action and shows the result. Reset needs a second press within 3 s.</summary>
        async void Fire(ButtonConfig b)
        {
            bool reset = b.Kind == ActionKind.Reset;
            if (reset)
            {
                if (!ReferenceEquals(armed, b) || DateTime.UtcNow - armedAt > TimeSpan.FromSeconds(3))
                {
                    armed = b;
                    armedAt = DateTime.UtcNow;
                    var plan = await Task.Run(() => Reset.MakePlan(b));
                    ShowResult(b, new Outcome(Glyphs.Reset, "Press again to reset", Reset.Describe(plan), Tone.Normal), true, 3000);
                    return;
                }
                armed = null;
                ShowResult(b, new Outcome(Glyphs.Reset, "Resetting…", "Closing apps", Tone.Normal), true, 15000);
            }
            var o = await Task.Run(() => ActionRunner.Run(b));
            if (o != null) ShowResult(b, o, reset, reset ? Math.Max(5000, (int)(cfg.PopupSeconds * 1000)) : null);
        }

        void ShowResult(ButtonConfig b, Outcome o, bool force, int? holdMs)
        {
            if (ReferenceEquals(Cur, b) && testResult != null)
            {
                testResult.Text = o.Title + (string.IsNullOrEmpty(o.Detail) ? "" : " · " + o.Detail);
                testResult.Foreground = o.Tone == Tone.Error ? Theme.Amber : Theme.Muted;
            }
            if (cfg.PopupOn || force) popup?.ShowOutcome(o, cfg, force, holdMs);
        }

        // ---------------- pad ----------------

        /// <summary>Hook callback, on the UI thread. Returns true to swallow the key.</summary>
        bool OnPadKey(int i, bool down, bool fresh)
        {
            if (fresh) Dispatcher.BeginInvoke(new Action(() => OnPress(i)));
            return cfg.Buttons[i].IsSet || IsVisible;
        }

        void OnPress(int i)
        {
            if (!cfg.PadProgrammed) { cfg.PadProgrammed = true; MarkDirty(); UpdateStatus(); }
            if (IsVisible)
            {
                Flash(i);
                if (sel != i && !recording) Select(i);
            }
            var b = cfg.Buttons[i];
            if (b.IsSet) Fire(b);
        }

        async void ProgramPad()
        {
            var r = MessageBox.Show(this,
                "This saves a new layout on the pad itself:\n\n• Buttons 1–6 → F19–F24\n• Knob → volume down / play-pause / volume up\n\nIt replaces the current F13–F18 layout and takes a second.",
                "Program pad", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (r != MessageBoxResult.OK) return;
            SetStatus("Programming the pad…", Theme.Muted);
            string err = await Task.Run(Pad.Program);
            if (err != null)
            {
                UpdateStatus();
                MessageBox.Show(this, err, "Program pad", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            cfg.PadProgrammed = true;
            cfg.Save();
            UpdateStatus();
            popup.ShowOutcome(new Outcome(Glyphs.Keys, "Pad programmed", "Buttons now send F19–F24. Press one to check.", Tone.On), cfg);
        }

        void UpdateStatus()
        {
            bool connected = preview || Pad.Connected();
            banner.Visibility = cfg.PadProgrammed || preview ? Visibility.Collapsed : Visibility.Visible;
            if (!connected) SetStatus("Pad not plugged in", Theme.Amber);
            else if (!cfg.PadProgrammed) SetStatus("Pad connected · needs programming", Theme.Amber);
            else SetStatus("Pad connected", Theme.Accent);
        }

        void SetStatus(string text, Brush color)
        {
            statusText.Text = text;
            statusText.Foreground = color == Theme.Accent ? Theme.Text : color;
            statusDot.Fill = color;
            if (tray != null) tray.Text = "PadDeck — " + text;
        }

        // ---------------- window / tray ----------------

        void SetupTray()
        {
            tray = new WF.NotifyIcon { Text = "PadDeck", Icon = Theme.TrayIcon(), Visible = true };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Open PadDeck", null, (s, e) => ShowFromTray());
            trayPopups = new WF.ToolStripMenuItem("Popups") { CheckOnClick = true, Checked = cfg.PopupOn };
            trayPopups.Click += (s, e) => { cfg.PopupOn = trayPopups.Checked; popupSw.IsChecked = cfg.PopupOn; MarkDirty(); };
            menu.Items.Add(trayPopups);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!quitting && cfg.CloseToTray)
            {
                e.Cancel = true;
                cfg.Save();
                Hide();
                return;
            }
            if (!quitting) { e.Cancel = true; Dispatcher.InvokeAsync(Quit); }
        }

        void Quit()
        {
            if (quitting) return;
            quitting = true;
            saveTimer.Stop();
            statusTimer.Stop();
            cfg.Save();
            hook?.Dispose();
            placer?.Dispose();
            popup?.Close();
            tray.Visible = false;
            tray.Dispose();
            Close();
            Application.Current.Shutdown();
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void DarkTitleBar()
        {
            var h = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(h, 20, ref on, 4);           // immersive dark mode
            int caption = 0x001C1917;                            // #17191C as COLORREF, matches the top bar
            DwmSetWindowAttribute(h, 35, ref caption, 4);
        }
    }

    static class Snapshot
    {
        public static void Save(FrameworkElement host, int w, int h, string path)
        {
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            host.UpdateLayout();
            const double scale = 1.5;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(path);
            enc.Save(fs);
        }
    }
}
