using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Islet
{
    public static class Log
    {
        private static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Islet", "islet.log");

        public static void Error(string what, Exception ex)
        {
            Write(what + ": " + ex);
        }

        public static void Info(string message)
        {
            Write(message);
        }

        private static void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                File.AppendAllText(Path, DateTime.Now.ToString("s") + " " + line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // Small per-user preferences in HKCU\Software\Islet.
    public static class Settings
    {
        private const string KeyPath = @"Software\Islet";

        // Keep a compact pill on screen instead of hiding until hovered.
        public static bool AlwaysShowCompact { get { return Get("AlwaysShowCompact", false); } set { Set("AlwaysShowCompact", value); } }
        // Charging, Bluetooth, silent mode and similar alerts.
        public static bool SystemAlerts { get { return Get("SystemAlerts", true); } set { Set("SystemAlerts", value); } }
        // A short "now playing" alert on every track change.
        public static bool SongAlerts { get { return Get("SongAlerts", false); } set { Set("SongAlerts", value); } }
        // Synced lyrics in the music player (fetched only while this is on).
        public static bool Lyrics { get { return Get("Lyrics", false); } set { Set("Lyrics", value); } }

        public static readonly string[] SearchEngines = { "Google", "Bing", "DuckDuckGo" };

        public static string SearchEngine
        {
            get
            {
                string value = GetString("SearchEngine");
                return Array.IndexOf(SearchEngines, value) >= 0 ? value : SearchEngines[0];
            }
            set { SetValue("SearchEngine", value, RegistryValueKind.String); }
        }

        // Pinned launcher apps, stored as "path\tname" lines.
        public static List<AppEntry> PinnedApps
        {
            get
            {
                var apps = new List<AppEntry>();
                string[] lines = null;
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                        if (key != null) lines = key.GetValue("PinnedApps") as string[];
                }
                catch (Exception ex)
                {
                    Log.Error("read pinned apps", ex);
                }
                foreach (string line in lines ?? new string[0])
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) apps.Add(new AppEntry { Path = line.Substring(0, tab), Name = line.Substring(tab + 1) });
                }
                return apps;
            }
            set
            {
                SetValue("PinnedApps", value.Select(a => a.Path + "\t" + a.Name).ToArray(), RegistryValueKind.MultiString);
            }
        }

        private static string GetString(string name)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                    return key != null ? key.GetValue(name) as string : null;
            }
            catch (Exception ex)
            {
                Log.Error("read setting " + name, ex);
                return null;
            }
        }

        private static void SetValue(string name, object value, RegistryValueKind kind)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key.SetValue(name, value, kind);
            }
            catch (Exception ex)
            {
                Log.Error("write setting " + name, ex);
            }
        }

        private static bool Get(string name, bool fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    object value = key != null ? key.GetValue(name) : null;
                    return value is int ? (int)value != 0 : fallback;
                }
            }
            catch (Exception ex)
            {
                Log.Error("read setting " + name, ex);
                return fallback;
            }
        }

        private static void Set(string name, bool value)
        {
            SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    // Picks a vivid representative colour from album art for the accents.
    public static class Accent
    {
        public static Color Of(BitmapSource art)
        {
            const int size = 24;
            var small = new FormatConvertedBitmap(
                new TransformedBitmap(art, new ScaleTransform((double)size / art.PixelWidth, (double)size / art.PixelHeight)),
                PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double pb = px[i], pg = px[i + 1], pr = px[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                // Weight saturated, bright pixels; near-greys barely count.
                double weight = Math.Pow((max - min) / 255.0, 2) * (max / 255.0) + 0.002;
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
            r /= total; g /= total; b /= total;

            double peak = Math.Max(r, Math.Max(g, b));
            if (peak - Math.Min(r, Math.Min(g, b)) < 25) return Color.FromRgb(235, 235, 235);
            double scale = 235 / Math.Max(peak, 1); // lift to a readable brightness on black
            return Color.FromRgb((byte)Math.Min(255, r * scale), (byte)Math.Min(255, g * scale), (byte)Math.Min(255, b * scale));
        }
    }
}
