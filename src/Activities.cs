using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Islet
{
    // Something the island can show. Each activity owns three presentations
    // with fixed sizes: Compact (the pill), Minimal (the split-off bubble) and
    // Expanded (the hover view). The island decides which one is visible.
    public abstract class Activity
    {
        public Grid Compact { get; protected set; }
        public Grid Minimal { get; protected set; }
        public Grid Expanded { get; protected set; }
        public string Glyph { get; protected set; }
        public Color Tint { get; protected set; }

        // Shown as a tab in the expanded view.
        public abstract bool IsAvailable { get; }

        // Running right now: eligible for the compact pill, bubble and sliver.
        public abstract bool IsLive { get; }

        // 0..1 for the sliver line, or negative when there is no progress.
        public virtual double Progress { get { return -1; } }

        // When the activity last became live; the newest one is primary.
        public DateTime LiveSince { get; protected set; }

        public event Action Changed;

        public virtual void Tick() { }

        protected void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        protected static Grid Sized(double width, double height)
        {
            return new Grid { Width = width, Height = height };
        }
    }

    public sealed class MusicActivity : Activity
    {
        private readonly MediaWatcher media;
        private MediaSnapshot snap;
        private DateTime pausedAt = DateTime.MinValue;

        private readonly Border compactArt, minimalArt, expandedArt, progressTrack, progressFill;
        private readonly TextBlock title, artist, source, elapsed, remaining, playGlyph;
        private readonly Visualizer compactBars, expandedBars;

        private readonly VolumeRow volume;

        // Shown instead of the player when nothing is playing: starts Spotify.
        private static readonly Color SpotifyGreen = Color.FromRgb(30, 215, 96);
        private readonly AppCatalog catalog;
        private readonly bool spotifyInstalled = MediaWatcher.SpotifyInstalled;
        private readonly FrameworkElement player, idle;
        private readonly Border idleIcon;
        private readonly TextBlock idleStatus;
        private bool starting;

        // Synced lyrics: in lyrics mode the big header gives way to a mini
        // header and three lines (previous, current, next), and the view grows.
        private const double PlayerHeight = 170, LyricsHeight = 226;
        private const string GlyphLyrics = "";
        private readonly LyricsService lyricsService = new LyricsService();
        private readonly RowDefinition headerRow;
        private readonly FrameworkElement header, lyricsPanel;
        private readonly Border miniArt;
        private readonly TextBlock miniTitle, miniArtist, previousLine, currentLine, nextLine, lyricsGlyph;
        private bool lyricsOn = Settings.Lyrics;
        private Lyrics lyrics;
        private string lyricsKey, lyricsStatus;
        private DateTime lyricsRetryAt = DateTime.MinValue;
        private int shownLine = -2;

        public MusicActivity(MediaWatcher media, SystemAudio audio, AppCatalog catalog)
        {
            this.media = media;
            this.catalog = catalog;
            Glyph = Ui.GlyphMusic;
            Tint = Ui.Neutral;

            Compact = Sized(224, 36);
            compactArt = Ui.ArtTile(24, 7);
            compactArt.HorizontalAlignment = HorizontalAlignment.Left;
            compactArt.Margin = new Thickness(7, 0, 0, 0);
            compactBars = new Visualizer(4, 3, 16) { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 15, 0) };
            Compact.Children.Add(compactArt);
            Compact.Children.Add(compactBars);

            Minimal = Sized(36, 36);
            minimalArt = Ui.ArtTile(26, 13);
            minimalArt.HorizontalAlignment = HorizontalAlignment.Center;
            Minimal.Children.Add(minimalArt);

            Expanded = Sized(400, 170);
            var inner = new Grid { Margin = new Thickness(18, 18, 18, 4) };
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Expanded.Children.Add(inner);

            var headerGrid = new Grid();
            header = headerGrid;
            headerRow = inner.RowDefinitions[0];
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            expandedArt = Ui.ArtTile(80, 16);
            expandedArt.Cursor = Cursors.Hand;
            expandedArt.ToolTip = "Open player";
            expandedArt.MouseLeftButtonUp += (s, e) => media.OpenSourceApp();
            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) };
            title = Ui.Label(16, FontWeights.SemiBold, Ui.White);
            artist = Ui.Label(13, FontWeights.Normal, Ui.Secondary);
            source = Ui.Label(11, FontWeights.Normal, Ui.Tertiary);
            source.Margin = new Thickness(0, 4, 0, 0);
            meta.Children.Add(title);
            meta.Children.Add(artist);
            meta.Children.Add(source);
            expandedBars = new Visualizer(6, 3, 22) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 2, 0) };
            Grid.SetColumn(meta, 1);
            Grid.SetColumn(expandedBars, 2);
            headerGrid.Children.Add(expandedArt);
            headerGrid.Children.Add(meta);
            headerGrid.Children.Add(expandedBars);
            inner.Children.Add(headerGrid);

            var lyricsGrid = new Grid { Visibility = Visibility.Collapsed };
            lyricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            lyricsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var mini = new DockPanel();
            miniArt = Ui.ArtTile(40, 10);
            miniArt.Cursor = Cursors.Hand;
            miniArt.MouseLeftButtonUp += (s, e) => media.OpenSourceApp();
            DockPanel.SetDock(miniArt, Dock.Left);
            mini.Children.Add(miniArt);
            var miniText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            miniTitle = Ui.Label(14, FontWeights.SemiBold, Ui.White);
            miniArtist = Ui.Label(12, FontWeights.Normal, Ui.Secondary);
            miniText.Children.Add(miniTitle);
            miniText.Children.Add(miniArtist);
            mini.Children.Add(miniText);
            lyricsGrid.Children.Add(mini);
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 6, 2, 0) };
            previousLine = Ui.Label(12, FontWeights.Normal, Ui.Tertiary);
            currentLine = Ui.Label(17, FontWeights.SemiBold, Ui.White);
            currentLine.TextWrapping = TextWrapping.Wrap;
            currentLine.MaxHeight = 48;
            currentLine.Margin = new Thickness(0, 3, 0, 3);
            nextLine = Ui.Label(12, FontWeights.Normal, Ui.Secondary);
            lines.Children.Add(previousLine);
            lines.Children.Add(currentLine);
            lines.Children.Add(nextLine);
            Grid.SetRow(lines, 1);
            lyricsGrid.Children.Add(lines);
            lyricsPanel = lyricsGrid;
            inner.Children.Add(lyricsGrid);

            var timeline = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            elapsed = Ui.Digits(11, FontWeights.Normal, Ui.Secondary);
            remaining = Ui.Digits(11, FontWeights.Normal, Ui.Secondary);
            remaining.HorizontalAlignment = HorizontalAlignment.Right;
            progressFill = new Border { CornerRadius = new CornerRadius(2), Background = Ui.White, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            progressTrack = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = Ui.Track, Child = progressFill };
            // A taller transparent strip makes the thin timeline easy to click.
            var seekArea = new Border { Background = Brushes.Transparent, Height = 18, Cursor = Cursors.Hand, Child = progressTrack };
            seekArea.MouseLeftButtonUp += (s, e) =>
            {
                if (snap == null || snap.Duration <= TimeSpan.Zero || progressTrack.ActualWidth <= 0) return;
                double fraction = Math.Max(0, Math.Min(1, e.GetPosition(progressTrack).X / progressTrack.ActualWidth));
                media.Seek(TimeSpan.FromTicks((long)(snap.Duration.Ticks * fraction)));
                e.Handled = true;
            };
            Grid.SetColumn(seekArea, 1);
            Grid.SetColumn(remaining, 2);
            timeline.Children.Add(elapsed);
            timeline.Children.Add(seekArea);
            timeline.Children.Add(remaining);
            Grid.SetRow(timeline, 1);
            inner.Children.Add(timeline);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            TextBlock unused;
            controls.Children.Add(Ui.IconButton(Ui.GlyphPrevious, 15, 36, media.Previous, out unused));
            controls.Children.Add(Ui.IconButton(Ui.GlyphPlay, 19, 40, media.TogglePlayPause, out playGlyph));
            controls.Children.Add(Ui.IconButton(Ui.GlyphNext, 15, 36, media.Next, out unused));
            // Volume sits left of the transport buttons, so it never needs a view switch.
            volume = new VolumeRow(audio, 68, 13, 28, false) { HorizontalAlignment = HorizontalAlignment.Left };
            var lyricsButton = Ui.IconButton(GlyphLyrics, 14, 30, ToggleLyrics, out lyricsGlyph);
            lyricsButton.HorizontalAlignment = HorizontalAlignment.Right;
            lyricsButton.ToolTip = "Lyrics";
            var bottom = new Grid();
            bottom.Children.Add(volume);
            bottom.Children.Add(controls);
            bottom.Children.Add(lyricsButton);
            Grid.SetRow(bottom, 2);
            inner.Children.Add(bottom);
            player = inner;

            var idlePanel = new Grid { Margin = new Thickness(18, 18, 18, 4) };
            idlePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });
            idlePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var idleHeader = new DockPanel();
            idleIcon = new Border { Width = 80, Height = 80, CornerRadius = new CornerRadius(16), Background = Ui.ArtPlaceholder };
            DockPanel.SetDock(idleIcon, Dock.Left);
            idleHeader.Children.Add(idleIcon);
            var idleText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            var spotifyTitle = Ui.Label(16, FontWeights.SemiBold, Ui.White);
            spotifyTitle.Text = "Spotify";
            idleStatus = Ui.Label(13, FontWeights.Normal, Ui.Secondary);
            idleStatus.Text = "Not playing";
            idleText.Children.Add(spotifyTitle);
            idleText.Children.Add(idleStatus);
            idleHeader.Children.Add(idleText);
            idlePanel.Children.Add(idleHeader);

            var playContent = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            playContent.Children.Add(Ui.Glyph(Ui.GlyphPlay, 13, Brushes.Black));
            var playLabel = Ui.Label(14, FontWeights.SemiBold, Brushes.Black);
            playLabel.Text = "Play";
            playLabel.Margin = new Thickness(8, 0, 0, 0);
            playContent.Children.Add(playLabel);
            var playButton = Ui.Pressable(playContent, 132, 38, 19, Ui.Frozen(SpotifyGreen), PlaySpotify);
            playButton.HorizontalAlignment = HorizontalAlignment.Center;
            playButton.VerticalAlignment = VerticalAlignment.Center;
            playButton.ToolTip = "Start Spotify and resume your last track";
            Grid.SetRow(playButton, 1);
            idlePanel.Children.Add(playButton);
            idle = idlePanel;
            Expanded.Children.Add(idle);

            catalog.IconLoaded += UpdateSpotifyIcon;
            media.Changed += OnMediaChanged;
            ShowPanel();
        }

        private void PlaySpotify()
        {
            if (starting) return;
            starting = true;
            idleStatus.Text = "Starting Spotify…";
            media.PlaySpotify(status =>
            {
                // Null means done; a message without an ellipsis is a final error.
                if (status == null || !status.EndsWith("…")) starting = false;
                idleStatus.Text = status ?? "Not playing";
            });
        }

        private void ShowPanel()
        {
            bool playing = snap != null;
            player.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            idle.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            if (!playing) UpdateSpotifyIcon();
            ApplyLyricsLayout();
        }

        private void ToggleLyrics()
        {
            lyricsOn = !lyricsOn;
            Settings.Lyrics = lyricsOn;
            ApplyLyricsLayout();
            if (lyricsOn) LoadLyrics();
        }

        // Lyrics mode swaps the header and grows the view; the island morphs to fit.
        private void ApplyLyricsLayout()
        {
            bool show = lyricsOn && snap != null;
            header.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            lyricsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            headerRow.Height = new GridLength(show ? 136 : 80);
            lyricsGlyph.Foreground = lyricsOn ? Ui.White : Ui.Tertiary;
            double height = show ? LyricsHeight : PlayerHeight;
            if (Expanded.Height != height)
            {
                Expanded.Height = height;
                RaiseChanged();
            }
            if (show) ShowLyricLine(true);
        }

        // Fetches lyrics for the current track (only while lyrics mode is on).
        private async void LoadLyrics()
        {
            if (!lyricsOn || snap == null) return;
            string key = snap.Title + "\u0001" + snap.Artist;
            if (key == lyricsKey) return;
            lyricsKey = key;
            lyrics = null;
            lyricsStatus = "Searching lyrics…";
            ShowLyricLine(true);
            Lyrics found;
            bool failed;
            try
            {
                found = await lyricsService.Find(snap.Title, snap.Artist, snap.Album, snap.Duration);
                failed = lyricsService.LastLookupFailed;
            }
            catch (Exception ex)
            {
                Log.Error("lyrics", ex);
                found = null;
                failed = true;
            }
            if (key != lyricsKey) return; // the track changed meanwhile
            lyrics = found;
            if (failed)
            {
                // Service unreachable: say so, and let Tick retry later.
                lyricsStatus = "Lyrics service unavailable, retrying…";
                lyricsKey = null;
                lyricsRetryAt = DateTime.UtcNow.AddSeconds(30);
            }
            else
            {
                lyricsStatus = found == null ? "No lyrics found for this song" : null;
            }
            ShowLyricLine(true);
        }

        private void ShowLyricLine(bool force)
        {
            if (!lyricsOn || snap == null) return;
            if (lyrics == null || lyrics.Instrumental)
            {
                previousLine.Text = "";
                nextLine.Text = "";
                currentLine.Text = lyrics != null ? "♪ Instrumental" : lyricsStatus ?? "";
                currentLine.Foreground = Ui.Secondary;
                shownLine = -2;
                return;
            }
            int index = lyrics.IndexAt(snap.EstimatedPosition, snap.Duration);
            if (index == shownLine && !force) return;
            bool moved = index != shownLine;
            shownLine = index;
            previousLine.Text = LineText(index - 1);
            currentLine.Text = index < 0 ? "♪" : LineText(index);
            currentLine.Foreground = Ui.White;
            nextLine.Text = LineText(index + 1);
            miniArtist.Text = snap.Artist + (lyrics.Synced ? "" : " · approx. timing");
            if (moved)
                currentLine.BeginAnimation(UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(220)));
        }

        private string LineText(int index)
        {
            if (lyrics == null || index < 0 || index >= lyrics.Lines.Count) return "";
            string text = lyrics.Lines[index].Text;
            return text.Length == 0 ? "♪" : text; // empty LRC lines mark instrumental breaks
        }

        // The real Spotify icon from the app list; it may arrive a moment later.
        private void UpdateSpotifyIcon()
        {
            var app = catalog.Find("Spotify");
            var icon = app != null ? catalog.Icon(app) : null;
            if (icon != null)
            {
                var image = new Image { Source = icon, Width = 52, Height = 52 };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                idleIcon.Child = image;
            }
            else if (idleIcon.Child == null)
            {
                idleIcon.Child = Ui.Glyph(Ui.GlyphMusic, 30, Ui.Frozen(SpotifyGreen));
            }
        }

        public MediaSnapshot Snapshot { get { return snap; } }
        public bool IsPlaying { get { return snap != null && snap.IsPlaying; } }

        // With Spotify installed the tab stays, so music can start from the island.
        public override bool IsAvailable { get { return snap != null || spotifyInstalled; } }

        // Stays live for a while after pausing, like the phone does.
        public override bool IsLive
        {
            get { return snap != null && (snap.IsPlaying || DateTime.Now - pausedAt < TimeSpan.FromMinutes(2)); }
        }

        public override double Progress
        {
            get
            {
                if (snap == null || snap.Duration <= TimeSpan.Zero) return -1;
                return snap.EstimatedPosition.TotalSeconds / snap.Duration.TotalSeconds;
            }
        }

        public event Action<MediaSnapshot> TrackChanged;

        private void OnMediaChanged(MediaSnapshot next, bool trackChanged)
        {
            bool wasLive = IsLive;
            if (snap != null && snap.IsPlaying && (next == null || !next.IsPlaying)) pausedAt = DateTime.Now;
            snap = next;
            if (next != null)
            {
                Ui.SetArt(compactArt, next.Art);
                Ui.SetArt(minimalArt, next.Art);
                Ui.SetArt(expandedArt, next.Art);
                Ui.SetArt(miniArt, next.Art);
                miniTitle.Text = next.Title;
                miniArtist.Text = next.Artist;
                Tint = next.Accent;
                var accent = Ui.Frozen(next.Accent);
                compactBars.Accent = accent;
                expandedBars.Accent = accent;
                progressFill.Background = accent;
                volume.Accent = accent;
                title.Text = next.Title;
                artist.Text = next.Artist;
                source.Text = next.Source;
                playGlyph.Text = next.IsPlaying ? Ui.GlyphPause : Ui.GlyphPlay;
                if (next.IsPlaying && !wasLive) LiveSince = DateTime.Now;
            }
            ShowPanel();
            if (trackChanged) LoadLyrics();
            UpdateTimeline();
            if (trackChanged && next != null)
            {
                var handler = TrackChanged;
                if (handler != null) handler(next);
            }
            RaiseChanged();
        }

        // Called at frame rate while bars are visible. Returns true while moving.
        public bool Frame(double[] bands)
        {
            bool moving = false;
            if (Compact.Opacity > 0) moving |= compactBars.Update(bands);
            if (Expanded.Opacity > 0) moving |= expandedBars.Update(bands);
            return moving;
        }

        public override void Tick()
        {
            if (Expanded.Opacity <= 0) return;
            UpdateTimeline();
            volume.Refresh();
            if (lyricsOn)
            {
                // Not loaded yet (lyrics mode was already on) or a failed lookup is due a retry.
                if (lyricsKey == null && DateTime.UtcNow >= lyricsRetryAt) LoadLyrics();
                ShowLyricLine(false);
            }
        }

        private void UpdateTimeline()
        {
            if (snap == null || snap.Duration <= TimeSpan.Zero)
            {
                elapsed.Text = "";
                remaining.Text = "";
                progressFill.Width = 0;
                return;
            }
            var pos = snap.EstimatedPosition;
            elapsed.Text = Ui.FormatTime(pos);
            remaining.Text = "-" + Ui.FormatTime(snap.Duration - pos);
            progressFill.Width = Math.Max(0, Math.Min(1, pos.TotalSeconds / snap.Duration.TotalSeconds)) * progressTrack.ActualWidth;
        }
    }

    public sealed class TimerActivity : Activity
    {
        private static readonly int[] PresetMinutes = { 1, 3, 5, 10, 15, 25, 45, 60 };

        private TimeSpan total, remainingAtPause;
        private DateTime endsAt;
        private bool running, paused;

        private readonly TextBlock compactDigits, bigDigits, caption, pauseGlyph;
        private readonly ProgressRing ring;
        private readonly Border barFill, barTrack;
        private readonly FrameworkElement setup, live;

        public TimerActivity()
        {
            Glyph = Ui.GlyphTimer;
            Tint = Ui.Orange;
            var orange = Ui.Frozen(Ui.Orange);

            Compact = Sized(200, 36);
            var lead = Ui.Glyph(Ui.GlyphTimer, 15, orange);
            lead.HorizontalAlignment = HorizontalAlignment.Left;
            lead.Margin = new Thickness(15, 0, 0, 0);
            compactDigits = Ui.Digits(15, FontWeights.SemiBold, orange);
            compactDigits.HorizontalAlignment = HorizontalAlignment.Right;
            compactDigits.Margin = new Thickness(0, 0, 16, 0);
            Compact.Children.Add(lead);
            Compact.Children.Add(compactDigits);

            Minimal = Sized(36, 36);
            ring = new ProgressRing(22, 3, Ui.Orange) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Minimal.Children.Add(ring);

            Expanded = Sized(380, 150);

            // Setup: preset chips.
            var setupPanel = new StackPanel { Margin = new Thickness(20, 18, 20, 0) };
            var heading = Ui.Label(13, FontWeights.SemiBold, Ui.Secondary);
            heading.Text = "Start a timer";
            heading.Margin = new Thickness(4, 0, 0, 10);
            setupPanel.Children.Add(heading);
            var chips = new WrapPanel();
            foreach (int minutes in PresetMinutes)
            {
                int m = minutes;
                var chip = Ui.ChipButton(m < 60 ? m + " min" : "1 hour", () => Start(TimeSpan.FromMinutes(m)));
                chip.Margin = new Thickness(4, 4, 4, 4);
                chips.Children.Add(chip);
            }
            setupPanel.Children.Add(chips);
            setup = setupPanel;

            // Live: big countdown, bar and controls.
            var livePanel = new Grid { Margin = new Thickness(22, 14, 22, 0) };
            livePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            livePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            livePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new Grid();
            var stack = new StackPanel();
            caption = Ui.Label(12, FontWeights.SemiBold, orange);
            bigDigits = Ui.Digits(40, FontWeights.Light, orange);
            stack.Children.Add(caption);
            stack.Children.Add(bigDigits);
            top.Children.Add(stack);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            TextBlock unused;
            buttons.Children.Add(Ui.IconButton(Ui.GlyphAdd, 14, 38, () => Add(TimeSpan.FromMinutes(1)), out unused));
            buttons.Children.Add(Ui.IconButton(Ui.GlyphPause, 16, 42, TogglePause, out pauseGlyph));
            buttons.Children.Add(Ui.IconButton(Ui.GlyphCancel, 14, 38, Cancel, out unused));
            top.Children.Add(buttons);
            livePanel.Children.Add(top);
            barFill = new Border { CornerRadius = new CornerRadius(2.5), Background = orange, HorizontalAlignment = HorizontalAlignment.Left };
            barTrack = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = Ui.Track, Child = barFill, Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetRow(barTrack, 1);
            livePanel.Children.Add(barTrack);
            live = livePanel;

            Expanded.Children.Add(setup);
            Expanded.Children.Add(live);
            Refresh();
        }

        public event Action Finished;

        public override bool IsAvailable { get { return true; } }
        public override bool IsLive { get { return running || paused; } }

        public override double Progress
        {
            get { return IsLive && total > TimeSpan.Zero ? 1 - Remaining.TotalSeconds / total.TotalSeconds : -1; }
        }

        private TimeSpan Remaining
        {
            get
            {
                if (paused) return remainingAtPause;
                if (!running) return TimeSpan.Zero;
                var left = endsAt - DateTime.Now;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        private void Start(TimeSpan duration)
        {
            total = duration;
            endsAt = DateTime.Now + duration;
            running = true;
            paused = false;
            LiveSince = DateTime.Now;
            Refresh();
            RaiseChanged();
        }

        private void Add(TimeSpan extra)
        {
            if (!IsLive) return;
            total += extra;
            if (paused) remainingAtPause += extra;
            else endsAt += extra;
            Refresh();
        }

        private void TogglePause()
        {
            if (running)
            {
                remainingAtPause = Remaining;
                running = false;
                paused = true;
            }
            else if (paused)
            {
                endsAt = DateTime.Now + remainingAtPause;
                running = true;
                paused = false;
            }
            Refresh();
            RaiseChanged();
        }

        private void Cancel()
        {
            running = false;
            paused = false;
            Refresh();
            RaiseChanged();
        }

        public override void Tick()
        {
            if (running && Remaining <= TimeSpan.Zero)
            {
                running = false;
                Refresh();
                RaiseChanged();
                var handler = Finished;
                if (handler != null) handler();
                return;
            }
            if (IsLive) Refresh();
        }

        private void Refresh()
        {
            bool isLive = IsLive;
            setup.Visibility = isLive ? Visibility.Collapsed : Visibility.Visible;
            live.Visibility = isLive ? Visibility.Visible : Visibility.Collapsed;
            if (!isLive) return;
            string text = Ui.FormatTime(Remaining + TimeSpan.FromMilliseconds(999));
            if (compactDigits.Text != text)
            {
                compactDigits.Text = text;
                bigDigits.Text = text;
            }
            caption.Text = paused ? "Paused" : "Timer · ends " + endsAt.ToString("HH:mm");
            pauseGlyph.Text = paused ? Ui.GlyphPlay : Ui.GlyphPause;
            double progress = Math.Max(0, Progress);
            ring.Set(1 - progress);
            barFill.Width = Math.Max(0, barTrack.ActualWidth * (1 - progress));
        }
    }

    public sealed class StopwatchActivity : Activity
    {
        private TimeSpan accumulated;
        private DateTime startedAt;
        private bool running;

        private readonly TextBlock compactDigits, bigDigits, startGlyph;

        public StopwatchActivity()
        {
            Glyph = Ui.GlyphStopwatch;
            Tint = Ui.Blue;
            var blue = Ui.Frozen(Color.FromRgb(100, 180, 255));

            Compact = Sized(200, 36);
            var lead = Ui.Glyph(Ui.GlyphStopwatch, 15, blue);
            lead.HorizontalAlignment = HorizontalAlignment.Left;
            lead.Margin = new Thickness(15, 0, 0, 0);
            compactDigits = Ui.Digits(15, FontWeights.SemiBold, blue);
            compactDigits.HorizontalAlignment = HorizontalAlignment.Right;
            compactDigits.Margin = new Thickness(0, 0, 16, 0);
            Compact.Children.Add(lead);
            Compact.Children.Add(compactDigits);

            Minimal = Sized(36, 36);
            Minimal.Children.Add(Ui.Glyph(Ui.GlyphStopwatch, 15, blue));

            Expanded = Sized(380, 150);
            var panel = new Grid { Margin = new Thickness(22, 16, 22, 0) };
            var stack = new StackPanel();
            var caption = Ui.Label(12, FontWeights.SemiBold, Ui.Secondary);
            caption.Text = "Stopwatch";
            bigDigits = Ui.Digits(44, FontWeights.Light, Ui.White);
            stack.Children.Add(caption);
            stack.Children.Add(bigDigits);
            panel.Children.Add(stack);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            TextBlock unused;
            buttons.Children.Add(Ui.IconButton(Ui.GlyphReset, 14, 38, Reset, out unused));
            buttons.Children.Add(Ui.IconButton(Ui.GlyphPlay, 17, 46, Toggle, out startGlyph));
            panel.Children.Add(buttons);
            Expanded.Children.Add(panel);
            Refresh();
        }

        public override bool IsAvailable { get { return true; } }
        public override bool IsLive { get { return running || accumulated > TimeSpan.Zero; } }

        private TimeSpan Elapsed { get { return running ? accumulated + (DateTime.Now - startedAt) : accumulated; } }

        private void Toggle()
        {
            if (running)
            {
                accumulated += DateTime.Now - startedAt;
                running = false;
            }
            else
            {
                if (accumulated == TimeSpan.Zero) LiveSince = DateTime.Now;
                startedAt = DateTime.Now;
                running = true;
            }
            Refresh();
            RaiseChanged();
        }

        private void Reset()
        {
            running = false;
            accumulated = TimeSpan.Zero;
            Refresh();
            RaiseChanged();
        }

        public override void Tick()
        {
            if (running) Refresh();
        }

        private void Refresh()
        {
            var t = Elapsed;
            compactDigits.Text = Ui.FormatTime(t);
            bigDigits.Text = Ui.FormatTime(t) + "." + (t.Milliseconds / 100);
            startGlyph.Text = running ? Ui.GlyphPause : Ui.GlyphPlay;
        }
    }

    // Mute button + volume slider (+ optional percentage) bound to the system
    // output. Refresh() pulls the live level so the volume keys stay in sync.
    public sealed class VolumeRow : StackPanel
    {
        private readonly SystemAudio audio;
        private readonly VolumeSlider slider;
        private readonly TextBlock glyph, percent;
        private bool muted;

        public VolumeRow(SystemAudio audio, double sliderWidth, double glyphSize, double buttonSize, bool showPercent)
        {
            this.audio = audio;
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            var button = Ui.IconButton(Ui.GlyphVolume, glyphSize, buttonSize, ToggleMute, out glyph);
            button.Margin = new Thickness(0, 0, 4, 0);
            button.ToolTip = "Mute";
            Children.Add(button);
            slider = new VolumeSlider(sliderWidth);
            slider.Changed += level =>
            {
                audio.SetVolume(level);
                ShowState(level, false);
            };
            Children.Add(slider);
            if (showPercent)
            {
                percent = Ui.Digits(15, FontWeights.SemiBold, Ui.White);
                percent.Width = 52;
                percent.TextAlignment = TextAlignment.Right;
                Children.Add(percent);
            }
            // The wheel anywhere on the row nudges the volume.
            MouseWheel += (s, e) =>
            {
                slider.Nudge(e.Delta > 0 ? 0.02 : -0.02);
                e.Handled = true;
            };
        }

        public Brush Accent { set { slider.Accent = value; } }

        public void Nudge(double delta)
        {
            slider.Nudge(delta);
        }

        public void Refresh()
        {
            if (slider.IsDragging) return;
            double? level = audio.Volume();
            bool? isMuted = audio.IsMuted();
            IsEnabled = level.HasValue;
            ShowState(level ?? 0, isMuted ?? false);
        }

        private void ToggleMute()
        {
            audio.SetMuted(!muted);
            Refresh();
        }

        private void ShowState(double level, bool isMuted)
        {
            muted = isMuted;
            slider.Value = level;
            slider.Opacity = isMuted ? 0.45 : 1;
            glyph.Text = audio.IsInput ? (isMuted ? "" : Ui.GlyphMic) : Ui.VolumeGlyph(level, isMuted);
            if (percent != null) percent.Text = isMuted ? "Muted" : Math.Round(level * 100) + "%";
        }
    }

    // Output and input: which device is used, its volume and mute. Clicking a
    // device opens a picker that makes the chosen device the Windows default.
    public sealed class SoundActivity : Activity
    {
        private const string GlyphSpeaker = "";
        private const string GlyphChevron = "";
        private const string GlyphCheck = "";
        private const string GlyphBack = "";

        private readonly SystemAudio output, input;
        private readonly VolumeRow outputRow, inputRow;
        private readonly TextBlock outputName, inputName, pickerTitle;
        private readonly FrameworkElement main, picker;
        private readonly StackPanel pickerList;

        public SoundActivity(SystemAudio output, SystemAudio input)
        {
            this.output = output;
            this.input = input;
            Glyph = Ui.GlyphVolume;
            Tint = Ui.Neutral;
            Compact = Sized(130, 36);
            Expanded = Sized(400, 184);

            var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 0) };
            panel.Children.Add(DeviceButton(GlyphSpeaker, out outputName, () => OpenPicker(false)));
            outputRow = new VolumeRow(output, 262, 15, 32, true) { Margin = new Thickness(4, 2, 0, 8) };
            panel.Children.Add(outputRow);
            panel.Children.Add(DeviceButton(Ui.GlyphMic, out inputName, () => OpenPicker(true)));
            inputRow = new VolumeRow(input, 262, 15, 32, true) { Margin = new Thickness(4, 2, 0, 0) };
            panel.Children.Add(inputRow);
            main = panel;

            var pickerPanel = new DockPanel { Margin = new Thickness(16, 12, 16, 0), Visibility = Visibility.Collapsed };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            TextBlock unused;
            header.Children.Add(Ui.IconButton(GlyphBack, 12, 28, ClosePicker, out unused));
            pickerTitle = Ui.Label(13, FontWeights.SemiBold, Ui.White);
            pickerTitle.Margin = new Thickness(6, 0, 0, 0);
            header.Children.Add(pickerTitle);
            DockPanel.SetDock(header, Dock.Top);
            pickerPanel.Children.Add(header);
            pickerList = new StackPanel();
            pickerPanel.Children.Add(new ScrollViewer
            {
                Content = pickerList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Height = 136,
            });
            picker = pickerPanel;

            Expanded.Children.Add(main);
            Expanded.Children.Add(picker);
            Expanded.Background = Brushes.Transparent; // so the wheel works anywhere on the view
            Expanded.MouseWheel += (s, e) =>
            {
                if (picker.Visibility == Visibility.Visible) return; // the list scrolls instead
                outputRow.Nudge(e.Delta > 0 ? 0.02 : -0.02);
                e.Handled = true;
            };
        }

        public override bool IsAvailable { get { return true; } }
        public override bool IsLive { get { return false; } }

        // Pill showing the current device; the chevron hints that it opens a list.
        private static Border DeviceButton(string glyph, out TextBlock name, Action open)
        {
            var row = new DockPanel();
            var icon = Ui.Glyph(glyph, 14, Ui.Secondary);
            icon.Width = 26;
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var chevron = Ui.Glyph(GlyphChevron, 10, Ui.Tertiary);
            chevron.Margin = new Thickness(8, 0, 4, 0);
            DockPanel.SetDock(chevron, Dock.Right);
            row.Children.Add(chevron);
            name = Ui.Label(13, FontWeights.SemiBold, Ui.White);
            row.Children.Add(name);
            var button = Ui.Pressable(row, double.NaN, 30, 15, Ui.Chip, open, new Thickness(6, 0, 8, 0));
            button.Margin = new Thickness(0);
            button.ToolTip = "Choose device";
            return button;
        }

        private void OpenPicker(bool capture)
        {
            pickerTitle.Text = capture ? "Input" : "Output";
            pickerList.Children.Clear();
            var devices = AudioDevices.List(capture);
            if (devices.Count == 0)
            {
                var none = Ui.Label(12, FontWeights.Normal, Ui.Tertiary);
                none.Text = capture ? "No microphone found" : "No output device found";
                none.Margin = new Thickness(8, 6, 0, 0);
                pickerList.Children.Add(none);
            }
            foreach (var device in devices)
            {
                var d = device;
                var row = new DockPanel();
                var check = Ui.Glyph(d.IsDefault ? GlyphCheck : "", 12, Ui.Frozen(Ui.Blue));
                check.Width = 24;
                DockPanel.SetDock(check, Dock.Left);
                row.Children.Add(check);
                var label = Ui.Label(13, d.IsDefault ? FontWeights.SemiBold : FontWeights.Normal, d.IsDefault ? Ui.White : Ui.Secondary);
                label.Text = d.Name;
                row.Children.Add(label);
                var item = Ui.Pressable(row, double.NaN, 28, 9, Brushes.Transparent, () => Choose(d), new Thickness(4, 0, 8, 0));
                item.Margin = new Thickness(0, 0, 0, 2);
                pickerList.Children.Add(item);
            }
            main.Visibility = Visibility.Collapsed;
            picker.Visibility = Visibility.Visible;
        }

        private void Choose(AudioDevice device)
        {
            if (!device.IsDefault && !AudioDevices.SetDefault(device.Id))
                Log.Info("could not switch to " + device.Name);
            output.Invalidate();
            input.Invalidate();
            ClosePicker();
        }

        private void ClosePicker()
        {
            picker.Visibility = Visibility.Collapsed;
            main.Visibility = Visibility.Visible;
            Tick();
        }

        public override void Tick()
        {
            if (Expanded.Opacity <= 0)
            {
                if (picker.Visibility == Visibility.Visible) ClosePicker(); // start fresh next time
                return;
            }
            if (picker.Visibility == Visibility.Visible) return;
            outputRow.Refresh();
            inputRow.Refresh();
            outputName.Text = output.DeviceName ?? "No output device";
            inputName.Text = input.DeviceName ?? "No microphone";
        }
    }

    // The resting "control centre": time, battery, sound, privacy and devices.
    public sealed class HomeActivity : Activity
    {
        private readonly PrivacyWatcher privacy;
        private readonly BluetoothWatcher bluetooth;
        private readonly TextBlock compactClock, clock, date, batteryText, soundText, privacyText;
        private readonly BatteryIcon battery;
        private readonly StackPanel deviceList;
        private bool? muted;

        public HomeActivity(PrivacyWatcher privacy, BluetoothWatcher bluetooth)
        {
            this.privacy = privacy;
            this.bluetooth = bluetooth;
            Glyph = Ui.GlyphHome;
            Tint = Ui.Neutral;

            Compact = Sized(130, 36);
            compactClock = Ui.Digits(14, FontWeights.SemiBold, Ui.White);
            compactClock.HorizontalAlignment = HorizontalAlignment.Center;
            Compact.Children.Add(compactClock);

            Expanded = Sized(400, 150);
            var grid = new Grid { Margin = new Thickness(22, 16, 22, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });

            var left = new StackPanel();
            clock = Ui.Digits(46, FontWeights.Light, Ui.White);
            date = Ui.Label(13, FontWeights.Normal, Ui.Secondary);
            left.Children.Add(clock);
            left.Children.Add(date);
            grid.Children.Add(left);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
            battery = new BatteryIcon(22, 11);
            batteryText = Ui.Label(12, FontWeights.Normal, Ui.Secondary);
            right.Children.Add(Row(battery, batteryText));
            soundText = Ui.Label(12, FontWeights.Normal, Ui.Secondary);
            right.Children.Add(Row(Ui.Glyph(Ui.GlyphVolume, 13, Ui.Secondary), soundText));
            privacyText = Ui.Label(12, FontWeights.Normal, Ui.Secondary);
            right.Children.Add(Row(Ui.Glyph(Ui.GlyphMic, 13, Ui.Secondary), privacyText));
            deviceList = new StackPanel();
            right.Children.Add(deviceList);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            Expanded.Children.Add(grid);
            Tick();
        }

        private static FrameworkElement Row(FrameworkElement icon, TextBlock text)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7), LastChildFill = true };
            var iconBox = new Grid { Width = 28 };
            iconBox.Children.Add(icon);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            DockPanel.SetDock(iconBox, Dock.Left);
            row.Children.Add(iconBox);
            row.Children.Add(text);
            return row;
        }

        public override bool IsAvailable { get { return true; } }
        public override bool IsLive { get { return false; } }

        public void SetSound(bool? isMuted, double? level)
        {
            muted = isMuted;
            soundText.Text = isMuted == null ? "No output device"
                : isMuted.Value ? "Silent"
                : "Sound on" + (level.HasValue ? " · " + Math.Round(level.Value * 100) + "%" : "");
        }

        public override void Tick()
        {
            var now = DateTime.Now;
            string text = now.ToString("HH:mm");
            if (compactClock.Text != text)
            {
                compactClock.Text = text;
                clock.Text = text;
                date.Text = now.ToString("dddd d MMMM");
            }
            if (Expanded.Opacity <= 0) return;

            var power = Power.Read();
            if (power.HasBattery)
            {
                battery.Set(power.Percent, power.Charging ? Ui.Green : power.Percent <= 20 ? Ui.Red : Ui.Neutral);
                batteryText.Text = power.Percent + "%" + (power.Charging ? " · Charging" : "");
            }
            else
            {
                battery.Set(100, Ui.Green);
                batteryText.Text = "Plugged in";
            }

            var users = new List<string>();
            foreach (var app in privacy.Camera) users.Add(app + " (camera)");
            foreach (var app in privacy.Microphone) users.Add(app + " (mic)");
            privacyText.Text = users.Count == 0 ? "Mic and camera off" : string.Join(", ", users);
            privacyText.Foreground = users.Count == 0 ? Ui.Secondary : Ui.Frozen(Ui.Orange);

            deviceList.Children.Clear();
            int shown = 0;
            foreach (var device in bluetooth.Devices)
            {
                if (shown++ == 2) break;
                var label = Ui.Label(12, FontWeights.Normal, Ui.Secondary);
                label.Text = device.Name + (device.Battery.HasValue ? " · " + device.Battery.Value + "%" : "");
                deviceList.Children.Add(Row(Ui.Glyph(Ui.GlyphBluetooth, 13, Ui.Secondary), label));
            }
        }
    }

    // A transient notification shown in the island without stealing input.
    public sealed class Alert
    {
        public Grid Content;
        public TimeSpan Duration;
        public Activity Opens; // the expanded view hovering the alert leads to

        private static Grid Pill(double width, double height)
        {
            return new Grid { Width = width, Height = height };
        }

        private static Alert TwoSided(double width, FrameworkElement lead, string leadText, Brush leadBrush, FrameworkElement trail, double seconds)
        {
            var g = Pill(width, 38);
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            left.Children.Add(lead);
            var text = Ui.Label(14, FontWeights.SemiBold, leadBrush);
            text.Text = leadText;
            text.Margin = new Thickness(8, 0, 0, 0);
            text.MaxWidth = width - 120;
            left.Children.Add(text);
            g.Children.Add(left);
            trail.HorizontalAlignment = HorizontalAlignment.Right;
            trail.Margin = new Thickness(0, 0, 16, 0);
            g.Children.Add(trail);
            return new Alert { Content = g, Duration = TimeSpan.FromSeconds(seconds) };
        }

        public static Alert Charging(int percent, bool charging)
        {
            var color = charging ? Ui.Green : Ui.Neutral;
            var brush = Ui.Frozen(color);
            var icon = new BatteryIcon(22, 11);
            icon.Set(percent, color);
            var trail = new StackPanel { Orientation = Orientation.Horizontal };
            var pct = Ui.Digits(14, FontWeights.SemiBold, brush);
            pct.Text = percent + "%";
            pct.Margin = new Thickness(0, 0, 8, 0);
            trail.Children.Add(pct);
            trail.Children.Add(icon);
            return TwoSided(260, Ui.Glyph(Ui.GlyphBolt, 14, charging ? brush : Ui.Tertiary),
                charging ? "Charging" : "On battery", charging ? Ui.White : Ui.Secondary, trail, 2.8);
        }

        public static Alert LowBattery(int percent)
        {
            var red = Ui.Frozen(Ui.Red);
            var icon = new BatteryIcon(22, 11);
            icon.Set(percent, Ui.Red);
            var trail = new StackPanel { Orientation = Orientation.Horizontal };
            var pct = Ui.Digits(14, FontWeights.SemiBold, red);
            pct.Text = percent + "%";
            pct.Margin = new Thickness(0, 0, 8, 0);
            trail.Children.Add(pct);
            trail.Children.Add(icon);
            return TwoSided(270, Ui.Glyph(Ui.GlyphBolt, 14, red), "Low Battery", Ui.White, trail, 4);
        }

        public static Alert Bluetooth(BluetoothDeviceInfo device)
        {
            var blue = Ui.Frozen(Color.FromRgb(100, 180, 255));
            var trail = Ui.Label(13, FontWeights.SemiBold, device.Battery.HasValue ? Ui.Frozen(Ui.Green) : Ui.Secondary);
            trail.Text = device.Battery.HasValue ? device.Battery.Value + "%" : "Connected";
            return TwoSided(320, Ui.Glyph(Ui.GlyphHeadphones, 15, blue), device.Name, Ui.White, trail, 3.2);
        }

        public static Alert Silent(bool muted)
        {
            var color = muted ? Ui.Frozen(Ui.Red) : Ui.White;
            var trail = Ui.Label(13, FontWeights.SemiBold, muted ? Ui.Frozen(Ui.Red) : Ui.Secondary);
            trail.Text = muted ? "On" : "Off";
            return TwoSided(230, Ui.Glyph(muted ? Ui.GlyphMute : Ui.GlyphVolume, 15, color), "Silent Mode", Ui.White, trail, 2.2);
        }

        public static Alert TimerDone(TimerActivity timer)
        {
            var orange = Ui.Frozen(Ui.Orange);
            var trail = Ui.Label(14, FontWeights.SemiBold, orange);
            trail.Text = "Done";
            var alert = TwoSided(240, Ui.Glyph(Ui.GlyphTimer, 15, orange), "Timer", Ui.White, trail, 6);
            alert.Opens = timer;
            return alert;
        }

        public static Alert NowPlaying(MediaSnapshot snap, MusicActivity music)
        {
            var g = Pill(340, 58);
            var art = Ui.ArtTile(40, 11);
            Ui.SetArt(art, snap.Art);
            art.HorizontalAlignment = HorizontalAlignment.Left;
            art.Margin = new Thickness(10, 0, 0, 0);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(62, 0, 20, 0) };
            var caption = Ui.Label(11, FontWeights.Normal, Ui.Secondary);
            caption.Text = snap.Source.Length > 0 ? "Now playing · " + snap.Source : "Now playing";
            var title = Ui.Label(14, FontWeights.SemiBold, Ui.White);
            title.Text = snap.Title;
            text.Children.Add(caption);
            text.Children.Add(title);
            g.Children.Add(art);
            g.Children.Add(text);
            return new Alert { Content = g, Duration = TimeSpan.FromSeconds(3), Opens = music };
        }
    }
}
