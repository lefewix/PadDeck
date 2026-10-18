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

        public static bool IsGlyph(string spec, out string glyph)
        {
            glyph = null;
            if (spec == null || !spec.StartsWith("glyph:", StringComparison.OrdinalIgnoreCase)) return false;
            try { glyph = char.ConvertFromUtf32(Convert.ToInt32(spec.Substring(6), 16)); return true; }
            catch { return false; }
        }

        public static bool IsImage(string spec) => !string.IsNullOrWhiteSpace(spec) && !spec.StartsWith("glyph:", StringComparison.OrdinalIgnoreCase);

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

        /// <summary>The visual for a button: an image if it has one, otherwise a symbol.</summary>
        public static FrameworkElement Visual(ButtonConfig b, double size, Brush glyphBrush)
        {
            if (Load(b.Icon) is ImageSource img)
            {
                var im = new System.Windows.Controls.Image { Source = img, Width = size, Height = size, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                return im;
            }
            string glyph = IsGlyph(b.Icon, out var g) ? g : Core.Glyphs.For(b.Kind);
            return new System.Windows.Controls.TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = size * 0.8, Foreground = glyphBrush };
        }
    }
}
