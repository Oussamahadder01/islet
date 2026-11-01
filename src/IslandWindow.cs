using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Islet
{
    // Retracted: off-screen (or a thin sliver while something is live).
    // Resting:   the compact pill, when "always show" is enabled.
    // Alert:     a transient notification; never takes mouse input.
    // Engaged:   the pointer is on the island; expanded and interactive.
    public enum Presence { Retracted, Resting, Alert, Engaged }

    // The overlay is a per-pixel transparent, top-most, non-activating window
    // at the top centre of the primary screen. Unless the island is engaged the
    // whole window is click-through (WS_EX_TRANSPARENT), so browser tabs below
    // stay usable even while an alert or the sliver is on screen. The pointer
    // is polled instead of hooked, which costs nothing measurable.
    public sealed class IslandWindow : Window
    {
        private const double SurfaceWidth = 560, SurfaceHeight = 272; // fits the lyrics view (226) + tabs
        private const double VisibleTop = 6, SliverTop = 2, HiddenTop = -52;
        private const double TabsHeight = 34;
        private const double BubbleSize = 36, BubbleGap = 8;
        private const double TriggerHalfWidth = 130;
        private const double ExpandedRadius = 34;

        private struct Target
        {
            public FrameworkElement Layer, BubbleLayer;
            public double W, H, R, Top, BubbleRadius;
            public bool Sliver, Tabs;
        }

        private readonly MediaWatcher media;
        private readonly LoopbackSpectrum spectrum = new LoopbackSpectrum();
        private readonly SystemAudio systemAudio = new SystemAudio();
        private readonly PrivacyWatcher privacy;
        private readonly BluetoothWatcher bluetooth;

        private readonly AppCatalog catalog;
        private readonly LauncherActivity launcher;
        private readonly HomeActivity home;
        private readonly MusicActivity music;
        private readonly SoundActivity sound;
        private readonly TimerActivity timer;
        private readonly StopwatchActivity stopwatch;
        private readonly List<Activity> activities;

        private readonly Spring springW = new Spring(2.4, 0.68, 0, 120);
        private readonly Spring springH = new Spring(2.4, 0.74, 0, 24);
        private readonly Spring springR = new Spring(2.4, 0.9, 0, 12);
        private readonly Spring springTop = new Spring(2.3, 0.82, 0, HiddenTop);
        private readonly Spring springBubbleX = new Spring(2.1, 0.72, 0, SurfaceWidth / 2);
        private readonly Spring springBubbleR = new Spring(2.4, 0.7, 0, 0);

        private readonly GlassEffect glass;
        private readonly Backdrop backdrop;
        private readonly Border island, bubble;
        private readonly RectangleGeometry clip = new RectangleGeometry();
        private readonly Grid islandHost, bubbleHost, sliver;
        private readonly Border sliverFill;
        private readonly StackPanel tabs, privacyDots;
        private readonly Ellipse micDot, camDot;
        private readonly ScaleTransform bubbleScale = new ScaleTransform(1, 1);
        private readonly Canvas overlay;

        private readonly Dictionary<FrameworkElement, double> opacity = new Dictionary<FrameworkElement, double>();
        private readonly List<FrameworkElement> retired = new List<FrameworkElement>();
        private readonly Queue<Alert> alerts = new Queue<Alert>();
        private readonly double[] bands = new double[6];

        private readonly DispatcherTimer pointerTimer, tickTimer, barsTimer, captureTimer, alertTimer, fullscreenTimer;

        private const int SearchHotkeyId = 1;

        private IntPtr hwnd;
        private IntPtr previousForeground;
        private bool typing;
        private Native.Rect32 windowRect;
        private double dpiScale = 1;
        private bool interactive = true;
        private bool hiddenForFullscreen;

        private Presence presence = Presence.Retracted;
        private Target target;
        private Activity view;
        private Activity pinned;
        private Alert alert;
        private string liveSignature = "";

        private bool animating;
        private TimeSpan lastFrame;
        private int diagFrames, diagCaptures, diagRetargets;
        private string diagReason = "";
        private readonly bool diag = Environment.GetEnvironmentVariable("ISLET_DIAG") == "1";
        private double? hoverSince, leaveSince;
        private bool? lastMuted;
        private PowerState? lastPower;
        private int systemPollCountdown;

        public IslandWindow(MediaWatcher media)
        {
            this.media = media;
            privacy = new PrivacyWatcher(Dispatcher);
            bluetooth = new BluetoothWatcher(Dispatcher);

            Title = "Islet";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = SurfaceWidth;
            Height = SurfaceHeight;
            PlaceOnPrimaryScreen();

            catalog = new AppCatalog(Dispatcher);
            catalog.RefreshIfStale();
            launcher = new LauncherActivity(catalog);
            home = new HomeActivity(privacy, bluetooth);
            music = new MusicActivity(media, systemAudio, catalog);
            sound = new SoundActivity(systemAudio, new SystemAudio(true));
            timer = new TimerActivity();
            stopwatch = new StopwatchActivity();
            activities = new List<Activity> { launcher, home, music, sound, timer, stopwatch };
            view = launcher;

            launcher.FocusRequested += BeginTyping;
            launcher.CloseRequested += () => { EndTyping(true); Disengage(); };
            launcher.Launched += () => { EndTyping(false); Disengage(); };
            Deactivated += (s, e) => EndTyping(false); // the user clicked elsewhere
            PreviewKeyDown += (s, e) =>
            {
                // Esc always closes, wherever keyboard focus sits inside the island.
                if (e.Key != Key.Escape || !typing) return;
                EndTyping(true);
                Disengage();
                e.Handled = true;
            };

            var root = new Grid();
            Content = root;

            // Half resolution: bilinear upscaling pre-softens the frost and halves capture cost.
            backdrop = new Backdrop((int)SurfaceWidth / 2, (int)SurfaceHeight / 2);
            glass = GlassEffect.TryCreate();
            if (glass != null)
            {
                glass.Surface = new Point4D(SurfaceWidth, SurfaceHeight, 1 / SurfaceWidth, 1 / SurfaceHeight);
                glass.Tint = new Point4D(0.03, 0.03, 0.045, 0.42);
                var glassImage = new Image
                {
                    Source = backdrop.Image,
                    Width = SurfaceWidth,
                    Height = SurfaceHeight,
                    Stretch = Stretch.Fill,
                    Effect = glass,
                    IsHitTestVisible = false,
                };
                RenderOptions.SetBitmapScalingMode(glassImage, BitmapScalingMode.Linear);
                root.Children.Add(glassImage);
            }
            Brush fallback = glass == null ? Ui.Frozen(Color.FromArgb(235, 18, 18, 22)) : Brushes.Transparent;

            islandHost = new Grid();
            sliver = new Grid { Width = 58, Height = 7 };
            sliverFill = new Border { Height = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
            sliver.Children.Add(sliverFill);
            AddLayer(islandHost, sliver);
            foreach (var activity in activities)
            {
                AddLayer(islandHost, activity.Compact);
                AddLayer(islandHost, activity.Expanded);
                activity.Changed += OnActivityChanged;
            }
            tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
            AddLayer(islandHost, tabs);
            tabs.VerticalAlignment = VerticalAlignment.Bottom;

            island = new Border
            {
                Background = fallback,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Clip = clip,
                Child = islandHost,
            };
            island.MouseWheel += OnWheel;
            root.Children.Add(island);

            overlay = new Canvas { IsHitTestVisible = true };
            bubbleHost = new Grid();
            foreach (var activity in activities)
                if (activity.Minimal != null) AddLayer(bubbleHost, activity.Minimal);
            bubble = new Border
            {
                Width = BubbleSize,
                Height = BubbleSize,
                CornerRadius = new CornerRadius(BubbleSize / 2),
                Background = fallback,
                Child = bubbleHost,
                Clip = new EllipseGeometry(new Point(BubbleSize / 2, BubbleSize / 2), BubbleSize / 2, BubbleSize / 2),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = bubbleScale,
                Cursor = Cursors.Hand,
            };
            bubble.MouseLeftButtonUp += OnBubbleClick;
            overlay.Children.Add(bubble);

            micDot = new Ellipse { Width = 7, Height = 7, Fill = Ui.Frozen(Ui.Orange), Margin = new Thickness(0, 0, 4, 0) };
            camDot = new Ellipse { Width = 7, Height = 7, Fill = Ui.Frozen(Ui.Green), Margin = new Thickness(0, 0, 4, 0) };
            privacyDots = new StackPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
            privacyDots.Children.Add(camDot);
            privacyDots.Children.Add(micDot);
            overlay.Children.Add(privacyDots);
            root.Children.Add(overlay);

            pointerTimer = MakeTimer(TimeSpan.FromMilliseconds(33), PollPointer);
            tickTimer = MakeTimer(TimeSpan.FromSeconds(1), Tick);
            barsTimer = MakeTimer(TimeSpan.FromMilliseconds(33), AnimateBars);
            captureTimer = MakeTimer(TimeSpan.FromMilliseconds(150), CaptureBackdrop);
            alertTimer = MakeTimer(TimeSpan.FromSeconds(3), () => { alertTimer.Stop(); NextAlert(); });
            fullscreenTimer = MakeTimer(TimeSpan.FromSeconds(1.5), CheckFullscreen);
            if (diag)
            {
                // Opt-in counters for diagnosing idle cost.
                MakeTimer(TimeSpan.FromSeconds(5), () =>
                {
                    Log.Info(string.Format("diag presence={0} animating={1} frames={2} captures={3} retargets={4} bars={5} capture={6} tick={7}ms unsettled: {8}",
                        presence, animating, diagFrames, diagCaptures, diagRetargets, barsTimer.IsEnabled, captureTimer.IsEnabled, tickTimer.Interval.TotalMilliseconds, diagReason));
                    diagReason = "";
                    diagFrames = diagCaptures = diagRetargets = 0;
                }).Start();
            }

            music.TrackChanged += snap =>
            {
                if (Settings.SongAlerts) PushAlert(Alert.NowPlaying(snap, music));
            };
            timer.Finished += OnTimerFinished;
            privacy.Changed += () => { UpdatePrivacyDots(); Retarget(); };
            bluetooth.Changed += OnBluetooth;

            SourceInitialized += OnSourceInitialized;
            SystemEvents.DisplaySettingsChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Reposition));
            Loaded += (s, e) =>
            {
                privacy.Start();
                bluetooth.Start();
                Tick();
                pointerTimer.Start();
                tickTimer.Start();
                fullscreenTimer.Start();
                presence = RestingPresence();
                UpdatePrivacyDots();
                Retarget();
            };
            Closed += (s, e) => { spectrum.Stop(); backdrop.Dispose(); };
        }

        private static DispatcherTimer MakeTimer(TimeSpan interval, Action tick)
        {
            var t = new DispatcherTimer(DispatcherPriority.Normal) { Interval = interval };
            t.Tick += (s, e) => tick();
            return t;
        }

        private void AddLayer(Panel host, FrameworkElement layer)
        {
            layer.HorizontalAlignment = HorizontalAlignment.Center;
            if (!(layer is StackPanel)) layer.VerticalAlignment = VerticalAlignment.Top;
            layer.Opacity = 0;
            layer.Visibility = Visibility.Collapsed;
            layer.IsHitTestVisible = false;
            host.Children.Add(layer);
            opacity[layer] = 0;
        }

        public void ApplySettings()
        {
            if (presence == Presence.Retracted || presence == Presence.Resting) presence = RestingPresence();
            Retarget();
        }

        // ------------------------------------------------------------------ state

        private Presence RestingPresence()
        {
            return Settings.AlwaysShowCompact ? Presence.Resting : Presence.Retracted;
        }

        private List<Activity> LiveActivities()
        {
            return activities
                .Where(a => a.IsLive)
                .OrderByDescending(a => a == pinned)
                .ThenByDescending(a => a.LiveSince)
                .ToList();
        }

        private bool PrivacyInUse
        {
            get { return privacy.Microphone.Count > 0 || privacy.Camera.Count > 0; }
        }

        private void Retarget()
        {
            diagRetargets++;
            var live = LiveActivities();
            liveSignature = string.Join(",", live.Select(a => a.Glyph));
            if (!view.IsAvailable) view = home;
            var t = new Target();
            switch (presence)
            {
                case Presence.Engaged:
                    t.Layer = view.Expanded;
                    t.W = view.Expanded.Width;
                    t.H = view.Expanded.Height + TabsHeight;
                    t.R = ExpandedRadius;
                    t.Top = VisibleTop;
                    t.Tabs = true;
                    break;
                case Presence.Alert:
                    t.Layer = alert.Content;
                    t.W = alert.Content.Width;
                    t.H = alert.Content.Height;
                    t.R = t.H / 2;
                    t.Top = VisibleTop;
                    break;
                case Presence.Resting:
                    var primary = live.Count > 0 ? live[0] : home;
                    t.Layer = primary.Compact;
                    t.W = primary.Compact.Width;
                    t.H = primary.Compact.Height;
                    t.R = t.H / 2;
                    t.Top = VisibleTop;
                    if (live.Count > 1 && live[1].Minimal != null)
                    {
                        t.BubbleLayer = live[1].Minimal;
                        t.BubbleRadius = BubbleSize / 2;
                    }
                    break;
                default:
                    if (live.Count > 0 || PrivacyInUse)
                    {
                        // A thin glass sliver: the only trace left on screen.
                        t.Sliver = true;
                        t.Layer = sliver;
                        t.W = sliver.Width;
                        t.H = sliver.Height;
                        t.R = t.H / 2;
                        t.Top = SliverTop;
                        if (live.Count > 1) t.BubbleRadius = 3.5; // a second droplet
                        UpdateSliver(live);
                    }
                    else
                    {
                        t.W = 120;
                        t.H = 24;
                        t.R = 12;
                        t.Top = HiddenTop;
                    }
                    break;
            }
            target = t;
            if (presence == Presence.Engaged) RebuildTabs();
            UpdateSchedules();
            Wake();
        }

        private void UpdateSliver(List<Activity> live)
        {
            if (live.Count == 0)
            {
                sliverFill.Width = 0;
                return;
            }
            double progress = live[0].Progress;
            sliverFill.Background = Ui.Frozen(live[0].Tint);
            sliverFill.Width = progress < 0 ? sliver.Width - 14 : Math.Max(3, (sliver.Width - 14) * Math.Min(1, progress));
        }

        private Activity PreferredView()
        {
            var live = LiveActivities();
            // Something running wins; otherwise the search bar and pinned apps.
            return live.Count > 0 ? live[0] : launcher;
        }

        private void ShowView(Activity next)
        {
            if (next == view) return;
            if (typing && next != launcher) EndTyping(true);
            view = next;
            Retarget();
        }

        // Opens the island on the search bar with keyboard focus (Ctrl+Alt+Space).
        private void OpenSearch()
        {
            if (hiddenForFullscreen) return;
            if (presence != Presence.Engaged) Engage();
            ShowView(launcher);
            BeginTyping();
        }

        // The island never takes focus except for typing: only then is
        // WS_EX_NOACTIVATE lifted, and Esc hands focus back where it was.
        private void BeginTyping()
        {
            if (typing || hwnd == IntPtr.Zero) return;
            typing = true;
            previousForeground = Native.GetForegroundWindow();
            int ex = Native.GetWindowLong(hwnd, Native.GwlExStyle);
            Native.SetWindowLong(hwnd, Native.GwlExStyle, ex & ~Native.WsExNoActivate);
            SetInteractive(true);
            Activate();
            Native.SetForegroundWindow(hwnd);
            // The view may still be collapsed until the next animation frame, and a
            // collapsed TextBox silently refuses focus: show it, lay out, then focus,
            // and once more after layout in case activation was still settling.
            var layer = launcher.Expanded;
            layer.Visibility = Visibility.Visible;
            UpdateLayout();
            FocusSearchBox();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusSearchBox));
        }

        private void FocusSearchBox()
        {
            if (!typing) return;
            launcher.Box.Focus();
            Keyboard.Focus(launcher.Box);
        }

        private void EndTyping(bool restoreFocus)
        {
            if (!typing) return;
            typing = false;
            int ex = Native.GetWindowLong(hwnd, Native.GwlExStyle);
            Native.SetWindowLong(hwnd, Native.GwlExStyle, ex | Native.WsExNoActivate);
            Keyboard.ClearFocus();
            if (restoreFocus && previousForeground != IntPtr.Zero && previousForeground != hwnd)
                Native.SetForegroundWindow(previousForeground);
            previousForeground = IntPtr.Zero;
        }

        private void Engage()
        {
            hoverSince = null;
            leaveSince = null;
            bool wasHidden = presence == Presence.Retracted;
            view = alert != null && alert.Opens != null ? alert.Opens : PreferredView();
            ClearAlerts();
            presence = Presence.Engaged;
            if (wasHidden) CaptureBackdrop(); // the screen still shows no island
            SetInteractive(true);
            Retarget();
        }

        private void Disengage()
        {
            EndTyping(true);
            launcher.Reset();
            hoverSince = null;
            leaveSince = null;
            presence = RestingPresence();
            SetInteractive(false);
            Retarget();
        }

        private void OnActivityChanged()
        {
            Retarget();
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (presence != Presence.Engaged) return;
            var available = activities.Where(a => a.IsAvailable).ToList();
            int index = available.IndexOf(view);
            index = (index + (e.Delta < 0 ? 1 : -1) + available.Count) % available.Count;
            ShowView(available[index]);
            e.Handled = true;
        }

        private void OnBubbleClick(object sender, MouseButtonEventArgs e)
        {
            var live = LiveActivities();
            if (live.Count > 1) pinned = live[1];
            Retarget();
            e.Handled = true;
        }

        private void RebuildTabs()
        {
            tabs.Children.Clear();
            foreach (var activity in activities)
            {
                if (!activity.IsAvailable) continue;
                var a = activity;
                TextBlock glyph;
                var button = Ui.IconButton(a.Glyph, 13, 28, () =>
                {
                    ShowView(a);
                    if (a == launcher) BeginTyping(); // a click is a fine moment to take focus
                }, out glyph);
                button.Margin = new Thickness(3, 0, 3, 0);
                glyph.Foreground = a == view ? Ui.White : a.IsLive ? Ui.Frozen(a.Tint) : Ui.Tertiary;
                tabs.Children.Add(button);
            }
        }

        // ----------------------------------------------------------------- alerts

        private void PushAlert(Alert next)
        {
            if (hiddenForFullscreen) return;
            if (presence == Presence.Engaged)
            {
                // Already looking at the island: jump to the relevant view instead.
                if (next.Opens != null)
                {
                    view = next.Opens;
                    Retarget();
                }
                return;
            }
            if (alerts.Count >= 3) return;
            alerts.Enqueue(next);
            if (alert == null) NextAlert();
        }

        private void NextAlert()
        {
            if (alert != null) retired.Add(alert.Content);
            alert = null;
            if (alerts.Count == 0)
            {
                if (presence == Presence.Alert) presence = RestingPresence();
                Retarget();
                return;
            }
            bool wasHidden = presence == Presence.Retracted;
            alert = alerts.Dequeue();
            AddLayer(islandHost, alert.Content);
            presence = Presence.Alert;
            if (wasHidden) CaptureBackdrop();
            alertTimer.Interval = alert.Duration;
            alertTimer.Start();
            Retarget();
        }

        private void ClearAlerts()
        {
            alertTimer.Stop();
            alerts.Clear();
            if (alert != null) retired.Add(alert.Content);
            alert = null;
        }

        private void OnTimerFinished()
        {
            try
            {
                string alarm = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Alarm01.wav");
                if (File.Exists(alarm)) new SoundPlayer(alarm).Play();
                else SystemSounds.Exclamation.Play();
            }
            catch (Exception ex)
            {
                Log.Error("timer sound", ex);
            }
            PushAlert(Alert.TimerDone(timer));
        }

        private void OnBluetooth(BluetoothDeviceInfo device, bool connected)
        {
            if (!connected || !Settings.SystemAlerts) return;
            // Give the battery reading a moment to arrive before announcing.
            var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            delay.Tick += (s, e) =>
            {
                delay.Stop();
                if (bluetooth.Devices.Contains(device)) PushAlert(Alert.Bluetooth(device));
            };
            delay.Start();
        }

        // ------------------------------------------------------------- scheduling

        private void UpdateSchedules()
        {
            bool visible = presence != Presence.Retracted && !hiddenForFullscreen;
            tickTimer.Interval = TimeSpan.FromMilliseconds(visible ? 200 : 1000);

            if (glass != null && backdrop.ExcludesSelf && !hiddenForFullscreen && (visible || target.Sliver))
            {
                // Live glass while shown; a slow refresh is enough for the sliver.
                captureTimer.Interval = TimeSpan.FromMilliseconds(visible ? 150 : 5000);
                captureTimer.Start();
            }
            else
            {
                captureTimer.Stop();
            }

            if (target.Layer == music.Compact || target.Layer == music.Expanded) barsTimer.Start();
        }

        private void Tick()
        {
            foreach (var activity in activities) activity.Tick();

            var live = LiveActivities();
            string signature = string.Join(",", live.Select(a => a.Glyph));
            if (signature != liveSignature) Retarget();
            else if (target.Sliver) UpdateSliver(live);

            // System state is polled about once a second whatever the tick rate.
            if (--systemPollCountdown <= 0)
            {
                systemPollCountdown = (int)(1000 / tickTimer.Interval.TotalMilliseconds);
                PollSystem();
            }
        }

        private void PollSystem()
        {
            bool? muted = systemAudio.IsMuted();
            home.SetSound(muted, systemAudio.Volume());
            if (lastMuted.HasValue && muted.HasValue && muted != lastMuted && Settings.SystemAlerts)
                PushAlert(Alert.Silent(muted.Value));
            lastMuted = muted;

            var power = Power.Read();
            if (power.HasBattery && lastPower.HasValue && Settings.SystemAlerts)
            {
                var before = lastPower.Value;
                if (power.Charging != before.Charging) PushAlert(Alert.Charging(power.Percent, power.Charging));
                else if (!power.Charging && ((before.Percent > 20 && power.Percent <= 20) || (before.Percent > 10 && power.Percent <= 10)))
                    PushAlert(Alert.LowBattery(power.Percent));
            }
            lastPower = power;
        }

        private void AnimateBars()
        {
            bool shown = opacity[music.Compact] > 0 || opacity[music.Expanded] > 0;
            bool want = shown && music.IsPlaying && !hiddenForFullscreen;
            if (want) spectrum.Start();
            else spectrum.Stop();
            if (want) spectrum.GetBands(bands);
            else Array.Clear(bands, 0, bands.Length);
            bool moving = music.Frame(bands);
            if (!want && !moving) barsTimer.Stop();
        }

        private void CaptureBackdrop()
        {
            if (glass == null || hwnd == IntPtr.Zero) return;
            try
            {
                if (backdrop.Capture(windowRect)) diagCaptures++;
            }
            catch (Exception ex)
            {
                Log.Error("backdrop capture", ex);
            }
        }

        // ---------------------------------------------------------------- pointer

        private void PollPointer()
        {
            if (hwnd == IntPtr.Zero || hiddenForFullscreen) return;
            Native.Point32 p;
            if (!Native.GetCursorPos(out p)) return;
            double x = (p.X - windowRect.Left) / dpiScale;
            double y = (p.Y - windowRect.Top) / dpiScale;
            double now = Environment.TickCount / 1000.0;

            // The trigger is the very top edge of the screen around the centre:
            // moving down onto a tab leaves it before the dwell completes.
            bool inZone = y <= 1.5 && y >= -2 && Math.Abs(x - SurfaceWidth / 2) <= TriggerHalfWidth;

            if (presence != Presence.Engaged)
            {
                bool over = (presence == Presence.Resting || presence == Presence.Alert) && TargetBounds(0).Contains(x, y);
                if (inZone || over)
                {
                    if (!hoverSince.HasValue) hoverSince = now;
                    if (now - hoverSince.Value >= (over ? 0.12 : 0.22)) Engage();
                }
                else
                {
                    hoverSince = null;
                }
            }
            else
            {
                // While typing or dragging a slider the island stays open wherever the pointer goes.
                if (typing || Mouse.Captured != null || inZone || TargetBounds(16).Contains(x, y)) leaveSince = null;
                else if (!leaveSince.HasValue) leaveSince = now;
                else if (now - leaveSince.Value >= 0.3) Disengage();
            }
        }

        private Rect TargetBounds(double margin)
        {
            var r = new Rect((SurfaceWidth - target.W) / 2, target.Top, target.W, target.H);
            if (target.BubbleLayer != null)
                r.Union(new Rect(SurfaceWidth / 2 + target.W / 2 + BubbleGap, target.Top + target.H / 2 - BubbleSize / 2, BubbleSize, BubbleSize));
            r.Inflate(margin, margin);
            return r;
        }

        private void SetInteractive(bool value)
        {
            if (interactive == value || hwnd == IntPtr.Zero) return;
            interactive = value;
            int ex = Native.GetWindowLong(hwnd, Native.GwlExStyle);
            ex = value ? ex & ~Native.WsExTransparent : ex | Native.WsExTransparent;
            Native.SetWindowLong(hwnd, Native.GwlExStyle, ex);
        }

        // -------------------------------------------------------------- animation

        private void Wake()
        {
            if (animating) return;
            animating = true;
            lastFrame = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (lastFrame == TimeSpan.Zero) { lastFrame = now; return; }
            double dt = (now - lastFrame).TotalSeconds;
            if (dt <= 0) return; // Rendering can fire more than once per frame
            lastFrame = now;
            dt = Math.Min(dt, 0.1);
            diagFrames++;

            double w = Math.Max(1, springW.Update(dt, target.W));
            double h = Math.Max(1, springH.Update(dt, target.H));
            double r = Math.Max(0, Math.Min(springR.Update(dt, target.R), Math.Min(w, h) / 2));
            double top = springTop.Update(dt, target.Top);
            double left = (SurfaceWidth - w) / 2;

            island.Width = w;
            island.Height = h;
            island.Margin = new Thickness(0, top, 0, 0);
            if (glass == null) island.CornerRadius = new CornerRadius(r);
            clip.Rect = new Rect(0, 0, w, h);
            clip.RadiusX = r;
            clip.RadiusY = r;

            // The bubble rests beside the island, or retreats into its edge
            // where the glass merges it back in.
            bool hasBubble = target.BubbleRadius > 0;
            double bubbleRadius = Math.Max(0, springBubbleR.Update(dt, target.BubbleRadius));
            double bubbleX = springBubbleX.Update(dt, hasBubble
                ? SurfaceWidth / 2 + target.W / 2 + BubbleGap + target.BubbleRadius
                : SurfaceWidth / 2 + w / 2 - BubbleSize / 2);
            double bubbleY = top + h / 2;
            Canvas.SetLeft(bubble, bubbleX - BubbleSize / 2);
            Canvas.SetTop(bubble, bubbleY - BubbleSize / 2);
            double bubbleScaleValue = bubbleRadius / (BubbleSize / 2);
            bubbleScale.ScaleX = bubbleScale.ScaleY = bubbleScaleValue;
            bubble.Visibility = bubbleRadius > 0.3 ? Visibility.Visible : Visibility.Collapsed;
            if (glass != null) bubble.Background = Brushes.Transparent;

            if (glass != null)
            {
                glass.Island = new Point4D(left, top, w, h);
                glass.Bubble = new Point4D(bubbleX, bubbleY, bubbleRadius, Math.Max(4, bubbleRadius * 0.6));
                glass.Style = new Point4D(r, h < 12 ? 1.5 : 5, Math.Max(2, Math.Min(16, h * 0.33)), h < 12 ? 0.6 : 1);
            }

            // Privacy dots ride beside the pill; the expanded Home view names the apps instead.
            double dotsX = hasBubble && bubbleRadius > 5 ? bubbleX + bubbleRadius + 6 : left + w + 7;
            Canvas.SetLeft(privacyDots, dotsX);
            Canvas.SetTop(privacyDots, top + h / 2 - 3.5);
            privacyDots.Opacity = presence == Presence.Engaged ? 0 : 1;

            bool settled = springW.IsSettled && springH.IsSettled && springR.IsSettled && springTop.IsSettled
                && springBubbleX.IsSettled && springBubbleR.IsSettled;
            if (!settled && diag)
                diagReason = "springs w=" + springW.IsSettled + " h=" + springH.IsSettled + " r=" + springR.IsSettled
                    + " top=" + springTop.IsSettled + " bx=" + springBubbleX.IsSettled + " br=" + springBubbleR.IsSettled
                    + " | bx " + springBubbleX + " | w " + springW;

            foreach (var layer in opacity.Keys.ToList())
            {
                bool active = layer == target.Layer || layer == target.BubbleLayer || (layer == tabs && target.Tabs);
                double goal = active && !retired.Contains(layer) ? 1 : 0;
                double o = opacity[layer];
                // Outgoing content leaves fast; incoming content fades in as the shape grows.
                o += (goal - o) * (1 - Math.Exp(-(goal > o ? 10 : 26) * dt));
                if (Math.Abs(goal - o) < 0.01) o = goal;
                else
                {
                    settled = false;
                    if (diag) diagReason = "layer " + layer.Width + "x" + layer.Height + " o=" + o.ToString("0.000") + " goal=" + goal;
                }
                opacity[layer] = o;
                layer.Opacity = o;
                layer.Visibility = o > 0 ? Visibility.Visible : Visibility.Collapsed;
                layer.IsHitTestVisible = active;
                if (o == 0 && retired.Contains(layer))
                {
                    retired.Remove(layer);
                    opacity.Remove(layer);
                    ((Panel)layer.Parent).Children.Remove(layer);
                }
            }

            if (settled)
            {
                CompositionTarget.Rendering -= OnRendering;
                animating = false;
            }
        }

        private void UpdatePrivacyDots()
        {
            micDot.Visibility = privacy.Microphone.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            camDot.Visibility = privacy.Camera.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ------------------------------------------------------- window plumbing

        private void PlaceOnPrimaryScreen()
        {
            Left = (SystemParameters.PrimaryScreenWidth - SurfaceWidth) / 2;
            Top = 0;
        }

        private void Reposition()
        {
            PlaceOnPrimaryScreen();
            UpdateMetrics();
        }

        private void UpdateMetrics()
        {
            if (hwnd == IntPtr.Zero) return;
            Native.GetWindowRect(hwnd, out windowRect);
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                dpiScale = source.CompositionTarget.TransformToDevice.M11;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            // Tool window: no Alt+Tab entry. No-activate: clicks never steal focus.
            int ex = Native.GetWindowLong(hwnd, Native.GwlExStyle);
            Native.SetWindowLong(hwnd, Native.GwlExStyle, ex | Native.WsExToolWindow | Native.WsExNoActivate);
            SetInteractive(false);
            // ISLET_CAPTURABLE=1 keeps the island visible to screenshots (the glass
            // then refreshes only while hidden); used to inspect the visuals.
            if (glass != null && Environment.GetEnvironmentVariable("ISLET_CAPTURABLE") != "1")
                backdrop.ExcludeFromCapture(hwnd);
            UpdateMetrics();
            HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x02E0) Dispatcher.BeginInvoke(new Action(UpdateMetrics)); // WM_DPICHANGED
                if (msg == Native.WmHotkey && w.ToInt32() == SearchHotkeyId)
                {
                    handled = true;
                    OpenSearch();
                }
                return IntPtr.Zero;
            });
            if (!Native.RegisterHotKey(hwnd, SearchHotkeyId, Native.ModControl | Native.ModAlt | Native.ModNoRepeat, 0x20))
                Log.Info("Ctrl+Alt+Space is taken by another app; search stays available from the island");
            Closed += (s2, e2) => Native.UnregisterHotKey(hwnd, SearchHotkeyId);
        }

        // Step aside entirely while a fullscreen app owns the island's monitor.
        private void CheckFullscreen()
        {
            if (hwnd == IntPtr.Zero) return;
            bool fullscreen = IsForegroundFullscreen();
            if (fullscreen && !hiddenForFullscreen)
            {
                hiddenForFullscreen = true;
                if (presence == Presence.Engaged) Disengage();
                ClearAlerts();
                spectrum.Stop();
                Hide();
                UpdateSchedules();
            }
            else if (!fullscreen && hiddenForFullscreen)
            {
                hiddenForFullscreen = false;
                Show();
                presence = RestingPresence();
                Retarget();
            }
            if (!hiddenForFullscreen)
                Native.SetWindowPos(hwnd, Native.HwndTopmost, 0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
        }

        private bool IsForegroundFullscreen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == hwnd) return false;
            var cls = new StringBuilder(64);
            Native.GetClassName(fg, cls, cls.Capacity);
            string name = cls.ToString();
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd") return false;

            IntPtr monitor = Native.MonitorFromWindow(fg, Native.MonitorDefaultToNearest);
            if (monitor != Native.MonitorFromWindow(hwnd, Native.MonitorDefaultToNearest)) return false;
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
            Native.Rect32 rect;
            if (!Native.GetMonitorInfo(monitor, ref info) || !Native.GetWindowRect(fg, out rect)) return false;
            return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top &&
                   rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
        }
    }
}
