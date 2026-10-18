using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PadDeck.Ui
{
    /// <summary>
    /// Button icons. A spec is "" (the action's default symbol), "glyph:XXXX" (a Segoe Fluent symbol code point),
    /// or a file: .png/.jpg/.bmp/.ico shown as-is, .exe/.dll/.lnk shown with the app's own icon.
    /// </summary>
    public static class Icons
    {
        /// <summary>Curated symbols for the picker: (code point, name).</summary>
        public static readonly (string code, string name)[] Symbols =
        {
            ("E8F2", "Chat"), ("E8BD", "Message"), ("E943", "Code"), ("E756", "Terminal"), ("E774", "Web"),
            ("E80F", "Home"), ("E8B7", "Folder"), ("E8A5", "Document"), ("E7FC", "Game"), ("E91B", "Picture"),
            ("E722", "Camera"), ("E714", "Video"), ("E7C8", "Record"), ("E93E", "Stream"), ("E720", "Mic"),
            ("EC54", "Mic off"), ("E7F6", "Headphones"), ("E7F5", "Speaker"), ("E767", "Volume"), ("E74F", "Mute"),
            ("E768", "Play"), ("E769", "Pause"), ("E72C", "Refresh"), ("E895", "Sync"), ("E7E8", "Power"),
            ("E713", "Settings"), ("E945", "Lightning"), ("E734", "Star"), ("EB51", "Heart"), ("E8C8", "Copy"),
            ("E77F", "Paste"), ("E765", "Keyboard"), ("E70F", "Pen"), ("E7F4", "Monitor"), ("E71D", "Apps"),
            ("E8A7", "Open"), ("E9D9", "Chart"), ("E706", "Brightness"), ("E708", "Moon"), ("E8D6", "Music"),
        };

        /// <summary>Line-drawn versions of app logos, styled like the Segoe symbols (1.5 px strokes on a 24 px grid).</summary>
        public static readonly (string key, string name)[] Lines = { ("claude", "Claude"), ("comfyui", "ComfyUI"), ("obs", "OBS") };

        public static bool IsLine(string spec, out string key)
        {
            key = spec != null && spec.StartsWith("line:", StringComparison.OrdinalIgnoreCase) ? spec.Substring(5).ToLowerInvariant() : null;
            return key != null && LineGeometry(key) != null;
        }

        static readonly Dictionary<string, Geometry> lineCache = new Dictionary<string, Geometry>();

        /// <summary>Path data on a 24×24 grid, drawn 2..22 like the Segoe glyphs.</summary>
        public static Geometry LineGeometry(string key)
        {
            if (lineCache.TryGetValue(key, out var g)) return g;
            string data = key switch
            {
                "claude" => ClaudeSpark(),
                // Rounded tile with ComfyUI's slanted, angular "C".
                "comfyui" => "M7,3 H17 A4,4 0 0 1 21,7 V17 A4,4 0 0 1 17,21 H7 A4,4 0 0 1 3,17 V7 A4,4 0 0 1 7,3 Z " +
                             "M17.4,7 H10.6 L6.6,12.2 L9.6,17 H15.8",
                // OBS: outer ring with three swirling "commas".
                "obs" => "M21.5,12 A9.5,9.5 0 1 1 2.5,12 A9.5,9.5 0 1 1 21.5,12 Z " + ObsCommas(),
                _ => null,
            };
            g = data == null ? null : Geometry.Parse(data);
            g?.Freeze();
            lineCache[key] = g;
            return g;
        }

        /// <summary>Claude's spark: twelve rays of slightly uneven length around a small open centre.</summary>
        static string ClaudeSpark()
        {
            double[] len = { 10, 7.6, 9.2, 7.2, 10, 8, 9.6, 7.2, 10, 7.6, 9.2, 8 };
            var sb = new System.Text.StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < 12; i++)
            {
                double a = (i * 30 - 90) * Math.PI / 180;
                double r0 = 2.2, r1 = len[i];
                sb.AppendFormat(inv, "M{0:0.##},{1:0.##} L{2:0.##},{3:0.##} ", 12 + r0 * Math.Cos(a), 12 + r0 * Math.Sin(a), 12 + r1 * Math.Cos(a), 12 + r1 * Math.Sin(a));
            }
            return sb.ToString();
        }

        /// <summary>Three small circles 120° apart, each with a tail sweeping clockwise towards the next.</summary>
        static string ObsCommas()
        {
            var sb = new System.Text.StringBuilder();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < 3; i++)
            {
                double a = (i * 120 - 90) * Math.PI / 180;
                double cx = 12 + 4.4 * Math.Cos(a), cy = 12 + 4.4 * Math.Sin(a), r = 2.2;
                sb.AppendFormat(inv, "M{0:0.##},{1:0.##} A{2},{2} 0 1 1 {3:0.##},{1:0.##} A{2},{2} 0 1 1 {0:0.##},{1:0.##} Z ", cx + r, cy, r, cx - r);
                // tail: from the circle's outer side, an arc around the centre at radius 7 for 70°
                double t0 = a + 20 * Math.PI / 180, t1 = a + 90 * Math.PI / 180;
                sb.AppendFormat(inv, "M{0:0.##},{1:0.##} A7,7 0 0 1 {2:0.##},{3:0.##} ", 12 + 7 * Math.Cos(t0), 12 + 7 * Math.Sin(t0), 12 + 7 * Math.Cos(t1), 12 + 7 * Math.Sin(t1));
            }
            return sb.ToString();
        }

        /// <summary>A line icon as a shape. <paramref name="size"/> is the glyph font size to match: Segoe symbols
        /// fill about their whole em box, while these are drawn 2..22 on 24, so the box is scaled up by 24/20.</summary>
        public static FrameworkElement LineVisual(string key, double size, Brush brush) => new System.Windows.Controls.Viewbox
        {
            Width = size * 1.2, Height = size * 1.2, Stretch = Stretch.Uniform,
            Child = new System.Windows.Controls.Canvas
            {
                Width = 24, Height = 24,
                Children =
                {
                    new System.Windows.Shapes.Path
                    {
                        Data = LineGeometry(key), Stroke = brush, StrokeThickness = 1.3, // ≈ the glyphs' stroke after the 1.2 scale-up
                        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    },
                },
            },
        };

        public static bool IsGlyph(string spec, out string glyph)
        {
            glyph = null;
            if (spec == null || !spec.StartsWith("glyph:", StringComparison.OrdinalIgnoreCase)) return false;
            try { glyph = char.ConvertFromUtf32(Convert.ToInt32(spec.Substring(6), 16)); return true; }
            catch { return false; }
        }

        public static bool IsImage(string spec) => !string.IsNullOrWhiteSpace(spec)
            && !spec.StartsWith("glyph:", StringComparison.OrdinalIgnoreCase) && !spec.StartsWith("line:", StringComparison.OrdinalIgnoreCase);

        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The image for a file spec, or null if it can't be loaded.</summary>
        public static ImageSource Load(string spec)
        {
            if (!IsImage(spec)) return null;
            if (cache.TryGetValue(spec, out var hit)) return hit;
            ImageSource img = null;
            try
            {
                string path = Environment.ExpandEnvironmentVariables(spec.Trim().Trim('"'));
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".lnk") (path, ext) = ResolveShortcut(path);
                if (File.Exists(path))
                {
                    if (ext == ".exe" || ext == ".dll") img = FromExe(path);
                    else
                    {
                        // .ico files hold several sizes; take the largest.
                        var dec = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        img = dec.Frames.OrderByDescending(f => f.PixelWidth).First();
                    }
                    img?.Freeze();
                }
            }
            catch { img = null; }
            cache[spec] = img;
            return img;
        }

        static ImageSource FromExe(string path)
        {
            using var icon = System.Drawing.Icon.ExtractIcon(path, 0, 64) ?? System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }

        /// <summary>A shortcut's custom icon if it has one, otherwise its target.</summary>
        static (string path, string ext) ResolveShortcut(string lnk)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic sc = shell.CreateShortcut(lnk);
            string iconLoc = (string)sc.IconLocation; // "path,index"
            string iconPath = iconLoc?.Split(',')[0];
            string target = !string.IsNullOrWhiteSpace(iconPath) && File.Exists(Environment.ExpandEnvironmentVariables(iconPath))
                ? Environment.ExpandEnvironmentVariables(iconPath) : (string)sc.TargetPath;
            return (target, Path.GetExtension(target).ToLowerInvariant());
        }

        /// <summary>Copies a picked image into PadDeck's own folder, so moving or updating the original doesn't break it.</summary>
        public static string Keep(string file, int button)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".exe" || ext == ".dll" || ext == ".lnk") return file; // read from the app each time
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadDeck", "icons");
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, $"button{button + 1}-{DateTime.Now:yyyyMMddHHmmss}{ext}");
            File.Copy(file, dest, true);
            return dest;
        }

        /// <summary>The visual for a button: an image if it has one, otherwise a line icon or symbol.</summary>
        public static FrameworkElement Visual(ButtonConfig b, double size, Brush glyphBrush) => Draw(b.Icon, Core.Glyphs.For(b.Kind), size, glyphBrush);

        /// <summary>Draws an icon spec; <paramref name="fallbackGlyph"/> is used for "" or anything unreadable.</summary>
        public static FrameworkElement Draw(string spec, string fallbackGlyph, double size, Brush brush)
        {
            if (Load(spec) is ImageSource img)
            {
                var im = new System.Windows.Controls.Image { Source = img, Width = size, Height = size, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                return im;
            }
            // Glyphs fill about 0.8 of their font size; line icons are drawn at that size so the two match.
            if (IsLine(spec, out var key)) return LineVisual(key, size * 0.8, brush);
            string glyph = IsGlyph(spec, out var g) ? g : fallbackGlyph;
            return new System.Windows.Controls.TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = size * 0.8, Foreground = brush };
        }
    }
}
