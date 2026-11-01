using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Islet
{
    public sealed class MediaSnapshot
    {
        public string Title;
        public string Artist;
        public string Album;
        public string Source;
        public bool IsPlaying;
        public TimeSpan Position;
        public DateTimeOffset PositionUpdatedAt;
        public TimeSpan Duration;
        public BitmapSource Art;
        public Color Accent;

        // Apps report the position only occasionally; extrapolate while playing.
        public TimeSpan EstimatedPosition
        {
            get
            {
                TimeSpan p = Position;
                if (IsPlaying && PositionUpdatedAt.Year > 2000)
                    p += DateTimeOffset.Now - PositionUpdatedAt;
                if (p < TimeSpan.Zero) p = TimeSpan.Zero;
                if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
                return p;
            }
        }
    }

    // Watches the system "now playing" session (the same Global System Media
    // Transport Controls that drive the Windows volume flyout). All state is
    // owned by the UI thread; WinRT events arrive on pool threads and are
    // marshalled back before anything is read.
    public sealed class MediaWatcher
    {
        private static readonly Color DefaultAccent = Color.FromRgb(235, 235, 235);
        private const ulong MaxArtBytes = 8 * 1024 * 1024;

        private readonly Dispatcher ui;
        private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> onProperties;
        private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> onPlayback;
        private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> onTimeline;

        private GlobalSystemMediaTransportControlsSessionManager manager;
        private GlobalSystemMediaTransportControlsSession session;
        private int generation;
        private string trackKey;
        private string artKey;
        private BitmapSource art;
        private Color accent = DefaultAccent;

        public MediaWatcher(Dispatcher ui)
        {
            this.ui = ui;
            onProperties = (s, e) => Queue(false, true);
            onPlayback = (s, e) => Queue(false, false);
            onTimeline = (s, e) => Queue(false, false);
        }

        // Raised on the UI thread. Snapshot is null when nothing is playing;
        // the flag is true when the track itself changed.
        public event Action<MediaSnapshot, bool> Changed;

        public MediaSnapshot Current { get; private set; }

        public async void Start()
        {
            try
            {
                manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().ToTask();
                manager.CurrentSessionChanged += (s, e) => Queue(true, true);
                Queue(true, true);
            }
            catch (Exception ex)
            {
                Log.Error("media manager unavailable", ex);
            }
        }

        public void TogglePlayPause() { Send(s => s.TryTogglePlayPauseAsync().ToTask()); }
        public void Next() { Send(s => s.TrySkipNextAsync().ToTask()); }
        public void Previous() { Send(s => s.TrySkipPreviousAsync().ToTask()); }

        // Spotify registers the "spotify:" protocol whether it came from the
        // Microsoft Store or the website, so that is how both are found and started.
        public static bool SpotifyInstalled
        {
            get
            {
                using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("spotify"))
                    return key != null;
            }
        }

        // Starts playback on Spotify, launching it first if needed. Spotify is
        // minimized as it opens so it does not take over the screen, and its
        // last track resumes. Status updates go to the UI (null when done).
        public async void PlaySpotify(Action<string> status)
        {
            try
            {
                var running = FindSpotifySession();
                if (running != null)
                {
                    await running.TryPlayAsync().ToTask();
                    status(null);
                    return;
                }

                status("Starting Spotify…");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("spotify:") { UseShellExecute = true });

                // Wait for Spotify to publish its media session (it restores the last track paused).
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(400);
                    MinimizeSpotify();
                    var session = FindSpotifySession();
                    if (session == null) continue;
                    status("Starting playback…");
                    await Task.Delay(800); // let it finish loading the last track
                    for (int attempt = 0; attempt < 4; attempt++)
                    {
                        if (await session.TryPlayAsync().ToTask())
                        {
                            MinimizeSpotify();
                            status(null);
                            return;
                        }
                        await Task.Delay(800);
                    }
                    break;
                }

                // No session yet: Spotify still listens to the global media keys.
                if (System.Diagnostics.Process.GetProcessesByName("Spotify").Length > 0)
                {
                    MediaKeys.PlayPause();
                    MinimizeSpotify();
                    status(null);
                }
                else
                {
                    status("Couldn't start Spotify");
                }
            }
            catch (Exception ex)
            {
                Log.Error("play spotify", ex);
                status("Couldn't start Spotify");
            }
        }

        private GlobalSystemMediaTransportControlsSession FindSpotifySession()
        {
            if (manager == null) return null;
            foreach (var candidate in manager.GetSessions())
            {
                string id = candidate.SourceAppUserModelId ?? "";
                if (id.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0) return candidate;
            }
            return null;
        }

        private static void MinimizeSpotify()
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("Spotify"))
            {
                IntPtr window = process.MainWindowHandle;
                if (window != IntPtr.Zero && !Native.IsIconic(window)) Native.ShowWindow(window, 7); // SW_SHOWMINNOACTIVE
            }
        }

        public void Seek(TimeSpan position)
        {
            Send(s => s.TryChangePlaybackPositionAsync(position.Ticks).ToTask());
        }

        // Brings the playing app forward: packaged apps through their AUMID,
        // desktop apps ("Spotify.exe") through their main window.
        public void OpenSourceApp()
        {
            var s = session;
            if (s == null) return;
            try
            {
                string id = s.SourceAppUserModelId ?? "";
                if (id.Contains("!"))
                {
                    // The id comes from another app; only pass plain AUMID characters on.
                    if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9._!-]+$")) return;
                    System.Diagnostics.Process.Start("explorer.exe", "shell:AppsFolder\\" + id);
                    return;
                }
                string name = id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id.Substring(0, id.Length - 4) : id;
                foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    IntPtr window = process.MainWindowHandle;
                    if (window == IntPtr.Zero) continue;
                    if (Native.IsIconic(window)) Native.ShowWindow(window, 9); // SW_RESTORE
                    Native.SetForegroundWindow(window);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("open source app", ex);
            }
        }

        private async void Send(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
        {
            var s = session;
            if (s == null) return;
            try { await command(s); }
            catch (Exception ex) { Log.Error("media command failed", ex); }
        }

        private void Queue(bool resubscribe, bool fetchProperties)
        {
            ui.BeginInvoke(new Action(() => Refresh(resubscribe, fetchProperties)));
        }

        private async void Refresh(bool resubscribe, bool fetchProperties)
        {
            int gen = ++generation;
            if (resubscribe && manager != null)
            {
                var current = manager.GetCurrentSession();
                if (!ReferenceEquals(current, session))
                {
                    Unhook(session);
                    session = current;
                    Hook(session);
                    fetchProperties = true;
                }
            }

            var s = session;
            if (s == null)
            {
                trackKey = null;
                Publish(null, Current != null);
                return;
            }

            try
            {
                var snap = new MediaSnapshot();
                if (fetchProperties || Current == null)
                {
                    var props = await s.TryGetMediaPropertiesAsync().ToTask();
                    if (gen != generation) return;
                    snap.Title = props.Title ?? "";
                    snap.Artist = props.Artist ?? "";
                    snap.Album = props.AlbumTitle ?? "";

                    string key = snap.Title + "\u0001" + snap.Artist;
                    string wantedArt = key + (props.Thumbnail != null ? "|art" : "|none");
                    if (wantedArt != artKey)
                    {
                        // Album art for this key stays valid even if a newer refresh
                        // started meanwhile, so it is cached before the staleness check.
                        BitmapSource loaded = props.Thumbnail != null ? await LoadArt(props.Thumbnail) : null;
                        artKey = wantedArt;
                        art = loaded;
                        accent = loaded != null ? Accent.Of(loaded) : DefaultAccent;
                        if (gen != generation) return;
                    }
                }
                else
                {
                    snap.Title = Current.Title;
                    snap.Artist = Current.Artist;
                    snap.Album = Current.Album;
                }

                var playback = s.GetPlaybackInfo();
                var timeline = s.GetTimelineProperties();
                snap.Source = PrettySource(s.SourceAppUserModelId);
                snap.IsPlaying = playback != null &&
                    playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (timeline != null)
                {
                    snap.Position = timeline.Position;
                    snap.PositionUpdatedAt = timeline.LastUpdatedTime;
                    snap.Duration = timeline.EndTime - timeline.StartTime;
                }
                snap.Art = art;
                snap.Accent = accent;

                if (snap.Title.Length == 0)
                {
                    Publish(null, false);
                    return;
                }
                string newKey = snap.Title + "\u0001" + snap.Artist;
                bool trackChanged = newKey != trackKey;
                trackKey = newKey;
                Publish(snap, trackChanged);
            }
            catch (Exception ex)
            {
                // The session can vanish between calls (the app closed).
                Log.Error("media refresh failed", ex);
            }
        }

        private void Publish(MediaSnapshot snap, bool trackChanged)
        {
            Current = snap;
            var handler = Changed;
            if (handler != null) handler(snap, trackChanged);
        }

        private void Hook(GlobalSystemMediaTransportControlsSession s)
        {
            if (s == null) return;
            s.MediaPropertiesChanged += onProperties;
            s.PlaybackInfoChanged += onPlayback;
            s.TimelinePropertiesChanged += onTimeline;
        }

        private void Unhook(GlobalSystemMediaTransportControlsSession s)
        {
            if (s == null) return;
            try
            {
                s.MediaPropertiesChanged -= onProperties;
                s.PlaybackInfoChanged -= onPlayback;
                s.TimelinePropertiesChanged -= onTimeline;
            }
            catch (Exception ex)
            {
                Log.Error("media unsubscribe failed", ex);
            }
        }

        private static async Task<BitmapSource> LoadArt(IRandomAccessStreamReference reference)
        {
            try
            {
                using (var winrtStream = await reference.OpenReadAsync().ToTask())
                {
                    // Thumbnails are small; anything past the cap is not album art worth decoding.
                    if (winrtStream.Size == 0 || winrtStream.Size > MaxArtBytes) return null;
                    var bytes = new byte[(int)winrtStream.Size];
                    using (var reader = new DataReader(winrtStream))
                    {
                        uint loaded = await reader.LoadAsync((uint)bytes.Length).ToTask();
                        if (loaded != bytes.Length) return null;
                        reader.ReadBytes(bytes);
                    }
                    var buffer = new MemoryStream(bytes);
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 192;
                    image.StreamSource = buffer;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception ex)
            {
                Log.Error("album art failed", ex);
                return null;
            }
        }

        // "Spotify.exe" -> "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" -> "ZuneMusic"
        private static string PrettySource(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return "";
            string name = appId;
            int bang = name.LastIndexOf('!');
            if (bang >= 0) name = name.Substring(bang + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
            return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name.Substring(1) : name;
        }
    }
}
