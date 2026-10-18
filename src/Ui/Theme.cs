using System.Windows;
using System.Windows.Media;
using PadDeck.Core;

namespace PadDeck.Ui
{
    static class Theme
    {
        static Brush Get(string key) => (Brush)Application.Current.Resources[key];

        public static Brush Bg => Get("BgBrush");
        public static Brush Panel => Get("PanelBrush");
        public static Brush Panel2 => Get("Panel2Brush");
        public static Brush Hover => Get("HoverBrush");
        public static Brush Line => Get("LineBrush");
        public static Brush Track => Get("TrackBrush");
        public static Brush Text => Get("TextBrush");
        public static Brush Muted => Get("MutedBrush");
        public static Brush Accent => Get("AccentBrush");
        public static Brush AccentFill => Get("AccentFillBrush");
        public static Brush Amber => Get("AmberBrush");
        public static Brush Red => Get("RedBrush");
        public static Brush Green => Get("GreenBrush");
        public static Style Switch => (Style)Application.Current.Resources["Switch"];
        public static Style Seg => (Style)Application.Current.Resources["Seg"];

        public static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");
        public static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static Brush ForTone(Tone t) => t switch
        {
            Tone.On => Green,
            Tone.Off => Red,
            Tone.Error => Amber,
            _ => Accent,
        };

        public static Brush Tint(Brush b, double opacity)
        {
            var c = ((SolidColorBrush)b).Color;
            var r = new SolidColorBrush(c) { Opacity = opacity };
            r.Freeze();
            return r;
        }

        public static System.Drawing.Icon TrayIcon()
        {
            using var st = typeof(Theme).Assembly.GetManifestResourceStream("PadDeck.ico");
            return new System.Drawing.Icon(st, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
    }
}
