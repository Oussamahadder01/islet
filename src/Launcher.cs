using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Islet
{
    public sealed class AppEntry
    {
        public string Name;
        public string Path; // parsing name inside shell:AppsFolder (an AUMID or a file path)
    }

    // Every launchable app, desktop and Store alike, read from the shell's
    // AppsFolder (the list behind the Start menu). Enumeration and icon
    // extraction run on a private STA thread so typing never waits on the shell.
    public sealed class AppCatalog
    {
        private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
        private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        private readonly Dispatcher ui;
        private readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        private readonly Dictionary<string, BitmapSource> icons = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> iconsPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<AppEntry> apps = new List<AppEntry>();
        private DateTime loadedAt = DateTime.MinValue;

        public AppCatalog(Dispatcher ui)
        {
            this.ui = ui;
            var thread = new Thread(() => { foreach (var job in work.GetConsumingEnumerable()) job(); })
            {
                IsBackground = true,
                Name = "Islet shell",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public event Action IconLoaded;

        // Reloads the app list when it is older than a few minutes (new installs).
        public void RefreshIfStale()
        {
            if (DateTime.UtcNow - loadedAt < TimeSpan.FromMinutes(5)) return;
            loadedAt = DateTime.UtcNow;
            work.Add(() =>
            {
                try
                {
                    var list = Enumerate();
                    ui.BeginInvoke(new Action(() => apps = list));
                }
                catch (Exception ex)
                {
                    Log.Error("app catalog", ex);
                }
            });
        }

        private static List<AppEntry> Enumerate()
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            var list = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                object folder = Invoke(shell, "NameSpace", "shell:AppsFolder");
                object items = Invoke(folder, "Items");
                int count = (int)Get(items, "Count");
                for (int i = 0; i < count; i++)
                {
                    object item = Invoke(items, "Item", i);
                    string name = (string)Get(item, "Name");
                    string path = (string)Get(item, "Path");
                    Marshal.ReleaseComObject(item);
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || !seen.Add(name)) continue;
                    if (IsNoise(name, path)) continue;
                    list.Add(new AppEntry { Name = name, Path = path });
                }
                Marshal.ReleaseComObject(items);
                Marshal.ReleaseComObject(folder);
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
            return list;
        }

        private static bool IsNoise(string name, string path)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("uninstall") || lower.Contains("désinstall") || lower.Contains("desinstall")) return true;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".url" || ext == ".chm" || ext == ".txt" || ext == ".pdf" || ext == ".html" || ext == ".htm";
        }

        private static object Invoke(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }

        private static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        // The app with exactly this name, or null (or while the list is still loading).
        public AppEntry Find(string name)
        {
            return apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Best matches first: prefix, then word prefix, then substring, then initials.
        public List<AppEntry> Search(string query, int max)
        {
            query = query.Trim();
            var scored = new List<KeyValuePair<int, AppEntry>>();
            foreach (var app in apps)
            {
                int score = Score(app.Name, query);
                if (score >= 0) scored.Add(new KeyValuePair<int, AppEntry>(score, app));
            }
            return scored
                .OrderBy(p => p.Key)
                .ThenBy(p => p.Value.Name.Length)
                .Take(max)
                .Select(p => p.Value)
                .ToList();
        }

        private static int Score(string name, string query)
        {
            if (query.Length == 0) return -1;
            if (Compare.IsPrefix(name, query, Loose)) return 0;
            int at = Compare.IndexOf(name, query, Loose);
            if (at > 0 && !char.IsLetterOrDigit(name[at - 1])) return 1;
            if (at > 0) return 2;
            var initials = new string(name.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w[0]).ToArray());
            return query.Length > 1 && Compare.IsPrefix(initials, query, Loose) ? 3 : -1;
        }

        public void Launch(AppEntry app)
        {
            try
            {
                // explorer resolves both AUMIDs and file paths inside AppsFolder.
                System.Diagnostics.Process.Start("explorer.exe", "\"shell:AppsFolder\\" + app.Path.Replace("\"", "") + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("launch " + app.Name, ex);
            }
        }

        // Returns the cached icon, or null and loads it in the background
        // (IconLoaded fires when it arrives).
        public BitmapSource Icon(AppEntry app)
        {
            BitmapSource icon;
            if (icons.TryGetValue(app.Path, out icon)) return icon;
            if (!iconsPending.Add(app.Path)) return null;
            string path = app.Path;
            work.Add(() =>
            {
                BitmapSource loaded = ShellIcon.Load("shell:AppsFolder\\" + path, 64);
                ui.BeginInvoke(new Action(() =>
                {
                    iconsPending.Remove(path);
                    icons[path] = loaded;
                    var handler = IconLoaded;
                    if (handler != null) handler();
                }));
            });
            return null;
        }
    }

    // Icon of any shell item through IShellItemImageFactory, keeping its alpha.
    internal static class ShellIcon
    {
        public static BitmapSource Load(string parsingName, int size)
        {
            IShellItemImageFactory factory = null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
                if (factory.GetImage(new NativeSize { Width = size, Height = size }, IconOnly | BiggerSizeOk, out bitmap) != 0) return null;

                Bitmap info;
                GetObject(bitmap, Marshal.SizeOf(typeof(Bitmap)), out info);
                int w = info.Width, h = Math.Abs(info.Height);
                var header = new Native.BitmapInfoHeader
                {
                    Size = Marshal.SizeOf(typeof(Native.BitmapInfoHeader)),
                    Width = w,
                    Height = -h,
                    Planes = 1,
                    BitCount = 32,
                };
                var pixels = new byte[w * h * 4];
                IntPtr dc = Native.GetDC(IntPtr.Zero);
                try { GetDIBits(dc, bitmap, 0, (uint)h, pixels, ref header, 0); }
                finally { Native.ReleaseDC(IntPtr.Zero, dc); }

                bool hasAlpha = false;
                for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
                if (!hasAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

                var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
                source.Freeze();
                return source;
            }
            catch (Exception ex)
            {
                Log.Error("icon " + parsingName, ex);
                return null;
            }
            finally
            {
                if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        private const int BiggerSizeOk = 0x1, IconOnly = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { public int Width, Height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Bitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }

        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItemImageFactory item);
        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr obj, int size, out Bitmap bitmap);
        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref Native.BitmapInfoHeader info, uint usage);
    }

    // Search box, results and pinned app tiles.
    public sealed class LauncherActivity : Activity
    {
        public const int MaxPinned = 4;

        private sealed class Result
        {
            public string Title, Subtitle, Glyph;
            public AppEntry App;
            public Action Run;
        }

        private readonly AppCatalog catalog;
        private readonly TextBox box;
        private readonly TextBlock placeholder, hint;
        private readonly StackPanel pinnedRow, resultList;
        private readonly List<Result> results = new List<Result>();
        private int selected;

        public LauncherActivity(AppCatalog catalog)
        {
            this.catalog = catalog;
            Glyph = "";
            Tint = Ui.Neutral;
            Compact = Sized(130, 36);

            Expanded = Sized(400, 186);
            var root = new Grid { Margin = new Thickness(16, 16, 16, 0) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Expanded.Children.Add(root);

            box = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Ui.White,
                CaretBrush = Ui.White,
                SelectionBrush = Ui.Frozen(Ui.Blue),
                FontFamily = Ui.TextFont,
                FontSize = 15,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(34, 0, 12, 0),
                FocusVisualStyle = null,
            };
            placeholder = Ui.Label(15, FontWeights.Normal, Ui.Tertiary);
            placeholder.Text = "Search apps and the web";
            placeholder.Margin = new Thickness(36, 0, 0, 0);
            placeholder.IsHitTestVisible = false;
            var searchGlyph = Ui.Glyph("", 14, Ui.Secondary);
            searchGlyph.HorizontalAlignment = HorizontalAlignment.Left;
            searchGlyph.Margin = new Thickness(12, 0, 0, 0);
            var field = new Grid();
            field.Children.Add(searchGlyph);
            field.Children.Add(placeholder);
            field.Children.Add(box);
            var fieldBorder = new Border { CornerRadius = new CornerRadius(19), Background = Ui.Chip, Child = field, Cursor = Cursors.IBeam };
            fieldBorder.PreviewMouseLeftButtonDown += (s, e) => RaiseFocus();
            root.Children.Add(fieldBorder);

            var content = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetRow(content, 1);
            pinnedRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            resultList = new StackPanel { Visibility = Visibility.Collapsed };
            hint = Ui.Label(12, FontWeights.Normal, Ui.Tertiary);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.Margin = new Thickness(0, 0, 0, 6);
            content.Children.Add(pinnedRow);
            content.Children.Add(resultList);
            content.Children.Add(hint);
            root.Children.Add(content);

            box.TextChanged += (s, e) => UpdateResults();
            box.PreviewKeyDown += OnKey;
            catalog.IconLoaded += () => { RebuildPinned(); RenderResults(); };
            RebuildPinned();
        }

        public TextBox Box { get { return box; } }

        public event Action FocusRequested, CloseRequested, Launched;

        public override bool IsAvailable { get { return true; } }
        public override bool IsLive { get { return false; } }

        public void Reset()
        {
            box.Text = "";
            catalog.RefreshIfStale();
            RebuildPinned();
        }

        private void RaiseFocus()
        {
            catalog.RefreshIfStale();
            var handler = FocusRequested;
            if (handler != null) handler();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                var handler = CloseRequested;
                if (handler != null) handler();
            }
            else if (e.Key == Key.Down && results.Count > 0)
            {
                selected = (selected + 1) % results.Count;
                RenderResults();
            }
            else if (e.Key == Key.Up && results.Count > 0)
            {
                selected = (selected - 1 + results.Count) % results.Count;
                RenderResults();
            }
            else if (e.Key == Key.Enter && results.Count > 0)
            {
                Run(results[selected]);
            }
            else
            {
                return;
            }
            e.Handled = true;
        }

        private void Run(Result result)
        {
            result.Run();
            box.Text = "";
            var handler = Launched;
            if (handler != null) handler();
        }

        private void UpdateResults()
        {
            string query = box.Text.Trim();
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            results.Clear();
            selected = 0;
            if (query.Length > 0)
            {
                string url = AsUrl(query);
                if (url != null)
                    results.Add(new Result { Title = "Open " + query, Subtitle = "Website", Glyph = "", Run = () => OpenUrl(url) });
                foreach (var app in catalog.Search(query, url != null ? 3 : 4))
                {
                    var a = app;
                    results.Add(new Result { Title = a.Name, Subtitle = "App", App = a, Run = () => catalog.Launch(a) });
                }
                string engine = Settings.SearchEngine;
                results.Add(new Result
                {
                    Title = "Search " + engine + " for “" + query + "”",
                    Subtitle = "Web",
                    Glyph = "",
                    Run = () => OpenUrl(SearchUrl(engine, query)),
                });
            }
            bool searching = query.Length > 0;
            pinnedRow.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            resultList.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            RebuildPinned();
            RenderResults();
        }

        private void RenderResults()
        {
            resultList.Children.Clear();
            for (int i = 0; i < results.Count; i++)
            {
                var result = results[i];
                var row = new Grid { Height = 24 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                FrameworkElement icon = result.App != null ? IconImage(result.App, 18) : null;
                if (icon == null) icon = Ui.Glyph(result.Glyph ?? "", 13, Ui.Secondary);
                row.Children.Add(icon);

                var title = Ui.Label(13, FontWeights.Normal, Ui.White);
                title.Text = result.Title;
                Grid.SetColumn(title, 1);
                row.Children.Add(title);

                if (result.App != null)
                {
                    var app = result.App;
                    bool isPinned = Settings.PinnedApps.Any(p => p.Path == app.Path);
                    TextBlock glyph;
                    var pin = Ui.IconButton(isPinned ? "" : "", 11, 22, () => TogglePin(app), out glyph);
                    pin.ToolTip = isPinned ? "Unpin" : "Pin to the island";
                    glyph.Foreground = isPinned ? Ui.Frozen(Ui.Orange) : Ui.Tertiary;
                    Grid.SetColumn(pin, 2);
                    row.Children.Add(pin);
                }
                else
                {
                    var sub = Ui.Label(11, FontWeights.Normal, Ui.Tertiary);
                    sub.Text = result.Subtitle;
                    sub.Margin = new Thickness(0, 0, 8, 0);
                    Grid.SetColumn(sub, 2);
                    row.Children.Add(sub);
                }

                var r = result;
                var holder = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = i == selected ? Ui.Hover : Brushes.Transparent,
                    Child = row,
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(4, 0, 4, 0),
                };
                holder.MouseLeftButtonUp += (s, e) => { Run(r); e.Handled = true; };
                resultList.Children.Add(holder);
            }
            hint.Text = results.Count > 0 ? "↑↓ to choose · Enter to open · Esc to close" : PinnedHint();
            hint.Visibility = results.Count > 0 || Settings.PinnedApps.Count < MaxPinned ? Visibility.Visible : Visibility.Collapsed;
        }

        private string PinnedHint()
        {
            return Settings.PinnedApps.Count == 0
                ? "Search for an app and press its pin to keep it here"
                : "Right-click a tile to unpin";
        }

        private FrameworkElement IconImage(AppEntry app, double size)
        {
            var source = catalog.Icon(app);
            if (source == null) return null;
            var image = new Image { Source = source, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        private void RebuildPinned()
        {
            pinnedRow.Children.Clear();
            foreach (var app in Settings.PinnedApps)
            {
                var a = app;
                var tile = new StackPanel { Width = 72 };
                FrameworkElement icon = IconImage(a, 40) ?? Ui.Glyph("", 22, Ui.Secondary);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.Height = 40;
                tile.Children.Add(icon);
                var name = Ui.Label(11, FontWeights.Normal, Ui.Secondary);
                name.Text = a.Name;
                name.HorizontalAlignment = HorizontalAlignment.Center;
                name.Margin = new Thickness(0, 5, 0, 0);
                tile.Children.Add(name);
                var button = Ui.Pressable(tile, 80, 74, 14, Brushes.Transparent, () =>
                {
                    catalog.Launch(a);
                    var handler = Launched;
                    if (handler != null) handler();
                }, new Thickness(0, 8, 0, 0));
                button.ToolTip = a.Name;
                button.MouseRightButtonUp += (s, e) => { TogglePin(a); e.Handled = true; };
                pinnedRow.Children.Add(button);
            }
            if (Settings.PinnedApps.Count < MaxPinned)
            {
                var plus = new StackPanel { Width = 72 };
                var glyph = Ui.Glyph(Ui.GlyphAdd, 18, Ui.Secondary);
                glyph.Height = 40;
                plus.Children.Add(glyph);
                var label = Ui.Label(11, FontWeights.Normal, Ui.Tertiary);
                label.Text = "Add app";
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.Margin = new Thickness(0, 5, 0, 0);
                plus.Children.Add(label);
                pinnedRow.Children.Add(Ui.Pressable(plus, 80, 74, 14, Brushes.Transparent, RaiseFocus, new Thickness(0, 8, 0, 0)));
            }
            if (box.Text.Trim().Length == 0)
            {
                hint.Text = PinnedHint();
                hint.Visibility = Settings.PinnedApps.Count < MaxPinned ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void TogglePin(AppEntry app)
        {
            var pinned = Settings.PinnedApps;
            int index = pinned.FindIndex(p => p.Path == app.Path);
            if (index >= 0) pinned.RemoveAt(index);
            else if (pinned.Count < MaxPinned) pinned.Add(app);
            Settings.PinnedApps = pinned;
            RebuildPinned();
            RenderResults();
        }

        // "example.com", "www.x.org/path", "https://..." -> an http(s) URL; otherwise null.
        private static string AsUrl(string text)
        {
            if (text.Contains(" ") || !text.Contains(".")) return null;
            string candidate = text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? text : "https://" + text;
            Uri uri;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            string host = uri.Host;
            int dot = host.LastIndexOf('.');
            return dot > 0 && host.Length - dot > 2 ? uri.AbsoluteUri : null;
        }

        public static string SearchUrl(string engine, string query)
        {
            string q = Uri.EscapeDataString(query);
            switch (engine)
            {
                case "Bing": return "https://www.bing.com/search?q=" + q;
                case "DuckDuckGo": return "https://duckduckgo.com/?q=" + q;
                default: return "https://www.google.com/search?q=" + q;
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("open url", ex);
            }
        }
    }
}
