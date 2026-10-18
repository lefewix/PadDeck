using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PadDeck.Core;
using WF = System.Windows.Forms;

namespace PadDeck.Ui
{
    /// <summary>
    /// The on-screen popup after a button fires. It never takes focus and lets clicks through,
    /// so it's safe over a game.
    /// </summary>
    public sealed class PopupWindow : Window
    {
        readonly DispatcherTimer hold;
        readonly Border host;

        public PopupWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Theme.Ui;
            UseLayoutRounding = true;
            Content = host = new Border();

            hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            hold.Tick += (s, e) => { hold.Stop(); FadeOut(); };
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(h, -20).ToInt64();
                ex |= 0x08000000 | 0x80 | 0x20; // NOACTIVATE | TOOLWINDOW | TRANSPARENT (click-through)
                SetWindowLongPtr(h, -20, new IntPtr(ex));
            };
        }

        /// <summary>The popup's visual, also used for offscreen snapshots.</summary>
        public static FrameworkElement Build(Outcome o, string iconSpec = null)
        {
            var tone = Theme.ForTone(o.Tone);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            if (Icons.Load(iconSpec) != null)
            {
                icon.Children.Add(Icons.Draw(iconSpec, o.Glyph, 32, tone)); // a picture: shown as-is, no circle
            }
            else
            {
                icon.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Theme.Tint(tone, 0.18) });
                var mark = string.IsNullOrEmpty(iconSpec) ? Icons.Draw("", o.Glyph, 24, tone) : Icons.Draw(iconSpec, o.Glyph, 24, tone);
                mark.HorizontalAlignment = HorizontalAlignment.Center;
                mark.VerticalAlignment = VerticalAlignment.Center;
                icon.Children.Add(mark);
            }
            g.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360 };
            text.Children.Add(new TextBlock { Text = o.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(o.Detail))
                text.Children.Add(new TextBlock { Text = o.Detail, FontSize = 12, Foreground = o.Tone == Tone.Error ? Theme.Amber : Theme.Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            return new Border
            {
                Background = Theme.Tint(Theme.Panel, 0.97),
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 12, 22, 12),
                Margin = new Thickness(18),
                MinWidth = 220,
                Child = g,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black },
            };
        }

        /// <param name="force">Show even over a fullscreen game (used for Reset's "press again" confirmation).</param>
        /// <param name="holdMs">How long it stays fully visible; null = the user's popup duration.</param>
        /// <param name="iconSpec">The button's icon to show instead of the outcome's symbol (see <see cref="Icons"/>).</param>
        public void ShowOutcome(Outcome o, AppSettings cfg, bool force = false, int? holdMs = null, string iconSpec = null)
        {
            if (!force && cfg.PopupSkipFullscreen && FullscreenAppInFront()) return;
            bool wasVisible = IsVisible && Opacity > 0.5;
            host.Child = Build(o, iconSpec);
            hold.Stop();
            hold.Interval = TimeSpan.FromMilliseconds(holdMs ?? (int)(Math.Clamp(cfg.PopupSeconds, 0.5, 15) * 1000));
            BeginAnimation(OpacityProperty, null);
            if (!IsVisible) { Opacity = 0; Show(); }
            UpdateLayout();
            Place(cfg);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            // A short slide towards the screen edge's opposite side; skipped when it's just updating its text.
            if (!wasVisible)
            {
                var slide = new TranslateTransform();
                host.RenderTransform = slide;
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(cfg.PopupTop ? -14 : 14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            }
            hold.Start();
        }

        void FadeOut()
        {
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            a.Completed += (s, e) => { if (!hold.IsEnabled) Hide(); };
            BeginAnimation(OpacityProperty, a);
        }

        void Place(AppSettings cfg)
        {
            var screen = Array.Find(WF.Screen.AllScreens, s => s.DeviceName == cfg.PopupScreen) ?? WF.Screen.PrimaryScreen;
            var wa = screen.WorkingArea;
            double scale = GetDpiForSystem() / 96.0;
            Left = (wa.Left + wa.Width / 2.0) / scale - ActualWidth / 2;
            Top = cfg.PopupTop ? wa.Top / scale + 24 : wa.Bottom / scale - ActualHeight - 40;
            var h = new WindowInteropHelper(this).Handle;
            SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST, NOSIZE | NOMOVE | NOACTIVATE
        }

        /// <summary>True when a fullscreen game or app is in front (exclusive or borderless).</summary>
        public static bool FullscreenAppInFront()
        {
            try { return SHQueryUserNotificationState(out int s) == 0 && (s == 2 || s == 3); }
            catch { return false; }
        }

        [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern uint GetDpiForSystem();
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    }
}
