using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Forms = System.Windows.Forms;

namespace Islet
{
    public struct PowerState
    {
        public bool HasBattery;
        public bool Charging;
        public int Percent;
    }

    public static class Power
    {
        public static PowerState Read()
        {
            var status = Forms.SystemInformation.PowerStatus;
            var state = new PowerState();
            state.HasBattery = !status.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)
                && !status.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.Unknown);
            state.Charging = status.PowerLineStatus == Forms.PowerLineStatus.Online;
            state.Percent = (int)Math.Round(Math.Min(1f, status.BatteryLifePercent) * 100);
            return state;
        }
    }

    // Which apps currently use the microphone or camera. Windows records this
    // per app under CapabilityAccessManager\ConsentStore: an app is in use while
    // its LastUsedTimeStop is 0. A registry change notification wakes the scan.
    public sealed class PrivacyWatcher
    {
        private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        private readonly Dispatcher ui;
        private string micKey = "", camKey = "";

        public PrivacyWatcher(Dispatcher ui)
        {
            this.ui = ui;
            Microphone = new List<string>();
            Camera = new List<string>();
        }

        public event Action Changed;
        public List<string> Microphone { get; private set; }
        public List<string> Camera { get; private set; }

        public void Start()
        {
            var thread = new Thread(WatchLoop) { IsBackground = true, Name = "Islet privacy" };
            thread.Start();
        }

        private void WatchLoop()
        {
            using (var signal = new AutoResetEvent(false))
            {
                while (true)
                {
                    try
                    {
                        using (var store = Registry.CurrentUser.OpenSubKey(Root.TrimEnd('\\')))
                        {
                            if (store != null)
                                RegNotifyChangeKeyValue(store.Handle.DangerousGetHandle(), true, NotifyLastSet,
                                    signal.SafeWaitHandle.DangerousGetHandle(), true);
                            Publish(Scan("microphone"), Scan("webcam"));
                            // The notification is one-shot; the timeout is a safety net.
                            signal.WaitOne(TimeSpan.FromSeconds(30));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("privacy scan", ex);
                        Thread.Sleep(5000);
                    }
                }
            }
        }

        private static List<string> Scan(string capability)
        {
            var users = new List<string>();
            using (var key = Registry.CurrentUser.OpenSubKey(Root + capability))
            {
                if (key == null) return users;
                foreach (string name in key.GetSubKeyNames())
                {
                    using (var app = key.OpenSubKey(name))
                    {
                        if (app == null) continue;
                        if (name == "NonPackaged")
                        {
                            foreach (string exe in app.GetSubKeyNames())
                                using (var desktopApp = app.OpenSubKey(exe))
                                    if (InUse(desktopApp)) users.Add(PrettyDesktop(exe));
                        }
                        else if (InUse(app))
                        {
                            users.Add(PrettyPackaged(name));
                        }
                    }
                }
            }
            users.Sort(StringComparer.OrdinalIgnoreCase);
            return users;
        }

        private static bool InUse(RegistryKey app)
        {
            if (app == null) return false;
            object start = app.GetValue("LastUsedTimeStart"), stop = app.GetValue("LastUsedTimeStop");
            return start is long && (long)start != 0 && stop is long && (long)stop == 0;
        }

        // "C:#Program Files#Zoom#bin#Zoom.exe" -> "Zoom"
        private static string PrettyDesktop(string mangled)
        {
            string file = mangled.Substring(mangled.LastIndexOf('#') + 1);
            return Path.GetFileNameWithoutExtension(file);
        }

        // "Microsoft.WindowsCamera_8wekyb3d8bbwe" -> "WindowsCamera"
        private static string PrettyPackaged(string family)
        {
            string name = family.Split('_')[0];
            int dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1) : name;
        }

        private void Publish(List<string> mic, List<string> cam)
        {
            string newMic = string.Join("|", mic), newCam = string.Join("|", cam);
            if (newMic == micKey && newCam == camKey) return;
            micKey = newMic;
            camKey = newCam;
            ui.BeginInvoke(new Action(() =>
            {
                Microphone = mic;
                Camera = cam;
                var handler = Changed;
                if (handler != null) handler();
            }));
        }

        private const uint NotifyLastSet = 0x00000004;

        [DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(IntPtr key, bool watchSubtree, uint filter, IntPtr evt, bool asynchronous);
    }

    public sealed class BluetoothDeviceInfo
    {
        public string Id;
        public string Name;
        public int? Battery;
    }

    // Connected Bluetooth devices (classic and LE) through WinRT device
    // watchers. Battery levels come from the device property Windows exposes
    // for hands-free / LE battery reporting, read with SetupAPI off the UI thread.
    public sealed class BluetoothWatcher
    {
        private static readonly Guid BatteryFmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");
        private static readonly Guid NameFmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");

        private readonly Dispatcher ui;
        private readonly Dictionary<string, BluetoothDeviceInfo> devices = new Dictionary<string, BluetoothDeviceInfo>();
        private readonly List<DeviceWatcher> watchers = new List<DeviceWatcher>();
        private int enumerated;

        public BluetoothWatcher(Dispatcher ui)
        {
            this.ui = ui;
        }

        // Raised on the UI thread with the device and whether it just connected
        // (false for disconnects and battery refreshes).
        public event Action<BluetoothDeviceInfo, bool> Changed;

        public IEnumerable<BluetoothDeviceInfo> Devices { get { return devices.Values; } }

        public void Start()
        {
            try
            {
                Watch(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
                Watch(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
            }
            catch (Exception ex)
            {
                Log.Error("bluetooth watcher", ex);
            }
        }

        private void Watch(string selector)
        {
            var watcher = DeviceInformation.CreateWatcher(selector);
            watcher.Added += (w, info) =>
            {
                string id = info.Id, name = info.Name;
                ui.BeginInvoke(new Action(() => OnAdded(id, name)));
            };
            watcher.Removed += (w, update) =>
            {
                string id = update.Id;
                ui.BeginInvoke(new Action(() => OnRemoved(id)));
            };
            watcher.Updated += (w, update) => { };
            watcher.EnumerationCompleted += (w, o) => Interlocked.Increment(ref enumerated);
            watchers.Add(watcher);
            watcher.Start();
        }

        private void OnAdded(string id, string name)
        {
            if (string.IsNullOrEmpty(name) || devices.ContainsKey(id)) return;
            var device = new BluetoothDeviceInfo { Id = id, Name = name };
            devices[id] = device;
            bool announce = Volatile.Read(ref enumerated) >= watchers.Count; // skip devices already connected at startup
            Raise(device, announce);
            RefreshBattery(device, announce ? 1500 : 0);
        }

        private void OnRemoved(string id)
        {
            BluetoothDeviceInfo device;
            if (!devices.TryGetValue(id, out device)) return;
            devices.Remove(id);
            Raise(device, false);
        }

        public void RefreshBatteries()
        {
            foreach (var device in devices.Values) RefreshBattery(device, 0);
        }

        private void RefreshBattery(BluetoothDeviceInfo device, int delayMs)
        {
            string name = device.Name;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // The profile reporting battery appears a moment after connecting.
                if (delayMs > 0) Thread.Sleep(delayMs);
                int? level = ReadBattery(name);
                ui.BeginInvoke(new Action(() =>
                {
                    if (!devices.ContainsKey(device.Id) || level == device.Battery) return;
                    device.Battery = level;
                    Raise(device, false);
                }));
            });
        }

        private void Raise(BluetoothDeviceInfo device, bool connected)
        {
            var handler = Changed;
            if (handler != null) handler(device, connected);
        }

        private static int? ReadBattery(string deviceName)
        {
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
            if (set == InvalidHandle) return null;
            try
            {
                var data = new SpDevinfoData { Size = Marshal.SizeOf(typeof(SpDevinfoData)) };
                var nameKey = new DevPropKey { Fmtid = NameFmtid, Pid = 14 };
                var batteryKey = new DevPropKey { Fmtid = BatteryFmtid, Pid = 2 };
                var buffer = new byte[512];
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    uint type, required;
                    if (!SetupDiGetDeviceProperty(set, ref data, ref nameKey, out type, buffer, (uint)buffer.Length, out required, 0))
                        continue;
                    string friendly = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Max(0, required - 2));
                    if (friendly.IndexOf(deviceName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (SetupDiGetDeviceProperty(set, ref data, ref batteryKey, out type, buffer, (uint)buffer.Length, out required, 0)
                        && type == DevpropTypeByte && buffer[0] <= 100)
                        return buffer[0];
                }
                return null;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        private const uint DigcfPresent = 0x2, DigcfAllClasses = 0x4, DevpropTypeByte = 0x3;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey { public Guid Fmtid; public uint Pid; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW")]
        private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SpDevinfoData data, ref DevPropKey key,
            out uint type, byte[] buffer, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
