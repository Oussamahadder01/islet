using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Islet
{
    // One process owns the island (it holds MainLock). While "always on" is
    // enabled, a second process waits on that lock as a hot standby: if the
    // owner dies for any reason, even a forced kill, Windows releases the lock
    // and the standby takes over at once, then raises a fresh standby. The
    // scheduler task re-checks every minute in case both are gone.
    public static class Program
    {
        private const string MainLock = @"Local\Islet.SingleInstance";
        private const string StandbyLock = @"Local\Islet.Standby";
        internal const string QuitEventName = @"Local\Islet.Quit";
        private const string WaitForPrevious = "--wait-for-previous";
        private const string StandbyArg = "--standby";

        private static readonly DateTime StartedAt = DateTime.UtcNow;
        private static Process standby;
        private static bool shuttingDown;

        [STAThread]
        public static int Main(string[] args)
        {
            // Kept free of UI types so a standby process never loads WPF.
            if (args.Length > 0 && (args[0] == "--install" || args[0] == "--uninstall"))
                return Setup(args[0] == "--install");
            if (Array.IndexOf(args, StandbyArg) >= 0) return Standby();

            bool handover = Array.IndexOf(args, WaitForPrevious) >= 0;
            using (var main = new Mutex(false, MainLock))
            {
                if (!Acquire(main, handover ? 15000 : 0))
                {
                    main.Dispose();
                    return Standby(); // an island is already up: guard it instead
                }
                try { Run(); }
                finally { main.ReleaseMutex(); }
            }
            return 0;
        }

        private static int Setup(bool install)
        {
            if (install)
            {
                Installer.Install();
                Installer.RunTask();
            }
            else
            {
                Installer.Uninstall();
                Forms.MessageBox.Show("Islet has been removed.", "Islet");
            }
            return 0;
        }

        // Waits (no CPU) until the island's owner is gone, then becomes it.
        // Only one standby may exist; extra ones exit immediately.
        private static int Standby()
        {
            using (var standbyLock = new Mutex(false, StandbyLock))
            {
                if (!Acquire(standbyLock, 0)) return 0;
                using (var main = new Mutex(false, MainLock))
                using (var quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName))
                {
                    int signalled;
                    try { signalled = WaitHandle.WaitAny(new WaitHandle[] { main, quit }); }
                    catch (AbandonedMutexException ex) { signalled = ex.MutexIndex; } // owner died holding it
                    standbyLock.ReleaseMutex();
                    if (signalled != 0) return 0; // asked to stop
                    try { Run(); }
                    finally { main.ReleaseMutex(); }
                }
            }
            return 0;
        }

        private static bool Acquire(Mutex mutex, int timeoutMs)
        {
            try { return mutex.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { return true; } // previous holder died; it is ours now
        }

        private static void Run()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Error("fatal", e.ExceptionObject as Exception);
                // A live standby takes over by itself. Otherwise come straight
                // back, unless crashing right after start (the task paces that).
                bool standbyAlive = standby != null && !standby.HasExited;
                if (!standbyAlive && DateTime.UtcNow - StartedAt > TimeSpan.FromSeconds(30)) Relaunch();
            };

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Error("unhandled", e.Exception);
                e.Handled = true; // an overlay should survive a bad frame
            };

            using (var quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName))
            {
                quit.Reset();
                var media = new MediaWatcher(app.Dispatcher);
                var window = new IslandWindow(media);
                using (var tray = CreateTray(app, window, quit))
                {
                    window.Show();
                    media.Start();
                    // Raise a standby only once this owner has proven stable,
                    // so a crash at startup cannot spin into a tight loop.
                    var arm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    arm.Tick += (s, e) => { arm.Stop(); EnsureStandby(); };
                    arm.Start();
                    app.Run();
                    shuttingDown = true;
                    tray.Visible = false;
                }
            }
        }

        private static void EnsureStandby()
        {
            if (shuttingDown || !Installer.StartsWithWindows) return;
            if (standby != null && !standby.HasExited) return;
            try
            {
                standby = Process.Start(new ProcessStartInfo(Installer.CurrentExe, StandbyArg) { UseShellExecute = false });
                standby.EnableRaisingEvents = true;
                // If the standby is killed, raise another one a moment later.
                standby.Exited += (s, e) => Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var retry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    retry.Tick += (s2, e2) => { retry.Stop(); EnsureStandby(); };
                    retry.Start();
                }));
            }
            catch (Exception ex)
            {
                Log.Error("start standby", ex);
            }
        }

        private static void Relaunch()
        {
            try { Process.Start(Installer.CurrentExe, WaitForPrevious); }
            catch (Exception ex) { Log.Error("relaunch", ex); }
        }

        private static Forms.ToolStripMenuItem Toggle(string text, bool initial, Action<bool> apply)
        {
            var item = new Forms.ToolStripMenuItem(text) { CheckOnClick = true, Checked = initial };
            item.CheckedChanged += (s, e) => apply(item.Checked);
            return item;
        }

        private static Forms.NotifyIcon CreateTray(Application app, IslandWindow window, EventWaitHandle quit)
        {
            var alwaysShow = Toggle("Always show compact island", Settings.AlwaysShowCompact,
                v => { Settings.AlwaysShowCompact = v; window.ApplySettings(); });
            var systemAlerts = Toggle("System alerts (charging, Bluetooth, silent)", Settings.SystemAlerts,
                v => Settings.SystemAlerts = v);
            var songAlerts = Toggle("Song change alerts", Settings.SongAlerts, v => Settings.SongAlerts = v);

            var engines = new Forms.ToolStripMenuItem("Web search engine");
            foreach (string name in Settings.SearchEngines)
            {
                var item = new Forms.ToolStripMenuItem(name) { Checked = name == Settings.SearchEngine };
                item.Click += (s, e) =>
                {
                    Settings.SearchEngine = item.Text;
                    foreach (Forms.ToolStripMenuItem other in engines.DropDownItems) other.Checked = other == item;
                };
                engines.DropDownItems.Add(item);
            }

            // Always on: Restart only. To stop Islet, turn always-on off first.
            var exit = new Forms.ToolStripMenuItem("Exit", null, (s, e) => app.Shutdown());
            var restart = new Forms.ToolStripMenuItem("Restart Islet", null, (s, e) =>
            {
                // With a standby waiting, simply leaving hands the island over.
                if (standby == null || standby.HasExited) Relaunch();
                app.Shutdown();
            });

            Forms.ToolStripMenuItem startup;
            if (Installer.IsInstalled)
            {
                startup = Toggle("Start with Windows (always on)", Installer.StartsWithWindows, v =>
                {
                    try
                    {
                        Installer.StartsWithWindows = v;
                        if (v)
                        {
                            quit.Reset();
                            EnsureStandby();
                        }
                        else
                        {
                            quit.Set(); // dismiss the standby
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("toggle always on", ex);
                    }
                    exit.Visible = !v;
                });
                exit.Visible = !startup.Checked;
            }
            else
            {
                startup = new Forms.ToolStripMenuItem("Install and start with Windows", null, (s, e) =>
                {
                    try
                    {
                        Installer.Install();
                        Process.Start(Installer.InstalledExe, WaitForPrevious); // hand over to the installed copy
                        app.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("install", ex);
                        Forms.MessageBox.Show("Install failed: " + ex.Message, "Islet");
                    }
                });
            }

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(new Forms.ToolStripMenuItem("Islet") { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(alwaysShow);
            menu.Items.Add(systemAlerts);
            menu.Items.Add(songAlerts);
            menu.Items.Add(engines);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(startup);
            menu.Items.Add(restart);
            menu.Items.Add(exit);

            return new Forms.NotifyIcon
            {
                Icon = DrawIcon(),
                Text = "Islet · Ctrl+Alt+Space to search",
                ContextMenuStrip = menu,
                Visible = true,
            };
        }

        // A tiny black pill on a light ring, drawn at runtime so no .ico ships.
        private static Icon DrawIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var path = Pill(new RectangleF(2, 9, 28, 14)))
                using (var fill = new SolidBrush(Color.FromArgb(12, 12, 12)))
                using (var ring = new Pen(Color.FromArgb(230, 230, 235), 2))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(ring, path);
                }
                using (var dot = new SolidBrush(Color.FromArgb(80, 220, 120)))
                    g.FillEllipse(dot, 20, 13, 6, 6);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static GraphicsPath Pill(RectangleF r)
        {
            var path = new GraphicsPath();
            float d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            return path;
        }
    }
}
