using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Islet
{
    // Spectrum of what the default output device is playing, via WASAPI
    // loopback. Capture runs on its own MTA thread only while started (the
    // island shows music bars); bands are computed on demand by the UI.
    public sealed class LoopbackSpectrum
    {
        private const int FftSize = 1024;

        private readonly object gate = new object();
        private readonly float[] ring = new float[FftSize];
        private int ringPos;
        private int sampleRate = 48000;
        private long lastAudioTicks;

        private readonly double[] re = new double[FftSize], im = new double[FftSize];
        private readonly double[] window = new double[FftSize];
        private double runningPeak = 0.05;

        private Thread thread;
        private volatile bool running;

        public LoopbackSpectrum()
        {
            for (int i = 0; i < FftSize; i++)
                window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
        }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Islet loopback" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            running = false;
        }

        // Fills bands with 0..1 levels on log-spaced frequency bands.
        public void GetBands(double[] bands)
        {
            int n = bands.Length;
            int rate;
            lock (gate)
            {
                bool stale = DateTime.UtcNow.Ticks - lastAudioTicks > TimeSpan.TicksPerMillisecond * 250;
                if (!running || stale)
                {
                    Array.Clear(bands, 0, n);
                    return;
                }
                for (int i = 0; i < FftSize; i++)
                {
                    re[i] = ring[(ringPos + i) % FftSize] * window[i];
                    im[i] = 0;
                }
                rate = sampleRate;
            }
            Fft(re, im);

            double framePeak = 0;
            for (int b = 0; b < n; b++)
            {
                double lo = 60 * Math.Pow(14000.0 / 60, (double)b / n);
                double hi = 60 * Math.Pow(14000.0 / 60, (double)(b + 1) / n);
                int from = Math.Max(1, (int)(lo * FftSize / rate));
                int to = Math.Min(FftSize / 2 - 1, Math.Max(from, (int)(hi * FftSize / rate)));
                double sum = 0;
                for (int k = from; k <= to; k++) sum += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                double magnitude = sum / (to - from + 1) / (FftSize / 4.0);
                double db = 20 * Math.Log10(magnitude + 1e-9);
                bands[b] = Math.Max(0, Math.Min(1, (db + 62) / 52));
                framePeak = Math.Max(framePeak, bands[b]);
            }
            // Slow automatic gain so quiet and loud tracks both fill the bars.
            runningPeak = Math.Max(framePeak, runningPeak * 0.995);
            double gain = 1 / Math.Max(runningPeak, 0.35);
            for (int b = 0; b < n; b++) bands[b] = Math.Min(1, bands[b] * gain);
        }

        private void CaptureLoop()
        {
            while (running)
            {
                try
                {
                    RunCapture();
                }
                catch (Exception ex)
                {
                    // Device changes or exclusive-mode apps end the stream; retry.
                    Log.Error("loopback capture", ex);
                }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(100);
            }
        }

        private void RunCapture()
        {
            var enumerator = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumerator();
            CoreAudio.IMMDevice device = null;
            CoreAudio.IAudioClient client = null;
            CoreAudio.IAudioCaptureClient capture = null;
            IntPtr format = IntPtr.Zero;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(CoreAudio.Render, CoreAudio.Multimedia, out device));
                Guid clientId = typeof(CoreAudio.IAudioClient).GUID;
                object activated;
                Check(device.Activate(ref clientId, CoreAudio.ClsctxAll, IntPtr.Zero, out activated));
                client = (CoreAudio.IAudioClient)activated;
                Check(client.GetMixFormat(out format));

                int tag = Marshal.ReadInt16(format, 0) & 0xFFFF;
                int channels = Marshal.ReadInt16(format, 2);
                int rate = Marshal.ReadInt32(format, 4);
                int bits = Marshal.ReadInt16(format, 14);
                bool isFloat = tag == 3 || (tag == 0xFFFE && Marshal.ReadInt32(format, 24) == 3);
                if (!(isFloat && bits == 32) && bits != 16) throw new NotSupportedException("mix format " + tag + "/" + bits);

                Check(client.Initialize(0, CoreAudio.StreamFlagsLoopback, 2000000, 0, format, IntPtr.Zero));
                Guid captureId = typeof(CoreAudio.IAudioCaptureClient).GUID;
                object service;
                Check(client.GetService(ref captureId, out service));
                capture = (CoreAudio.IAudioCaptureClient)service;
                lock (gate) sampleRate = rate;
                Check(client.Start());

                float[] floats = new float[0];
                short[] shorts = new short[0];
                while (running)
                {
                    Thread.Sleep(15);
                    uint packet;
                    Check(capture.GetNextPacketSize(out packet));
                    while (packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong devicePosition, qpc;
                        Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpc));
                        bool silent = (flags & 2) != 0;
                        int count = (int)frames * channels;
                        if (!silent)
                        {
                            // One copy per packet into reusable buffers.
                            if (isFloat)
                            {
                                if (floats.Length < count) floats = new float[count];
                                Marshal.Copy(data, floats, 0, count);
                            }
                            else
                            {
                                if (shorts.Length < count) shorts = new short[count];
                                Marshal.Copy(data, shorts, 0, count);
                            }
                        }
                        lock (gate)
                        {
                            for (int f = 0; f < frames; f++)
                            {
                                float mono = 0;
                                if (!silent)
                                {
                                    int s = f * channels;
                                    for (int c = 0; c < channels; c++)
                                        mono += isFloat ? floats[s + c] : shorts[s + c] / 32768f;
                                    mono /= channels;
                                }
                                ring[ringPos] = mono;
                                ringPos = (ringPos + 1) % FftSize;
                            }
                            lastAudioTicks = DateTime.UtcNow.Ticks;
                        }
                        Check(capture.ReleaseBuffer(frames));
                        Check(capture.GetNextPacketSize(out packet));
                    }
                }
                client.Stop();
            }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                if (capture != null) Marshal.ReleaseComObject(capture);
                if (client != null) Marshal.ReleaseComObject(client);
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }

        private static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        private static void Fft(double[] xr, double[] xi)
        {
            int n = xr.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = xr[i]; xr[i] = xr[j]; xr[j] = t;
                    t = xi[i]; xi[i] = xi[j]; xi[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2 * Math.PI / len;
                double wr = Math.Cos(angle), wi = Math.Sin(angle);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double tr = xr[b] * cr - xi[b] * ci, ti = xr[b] * ci + xi[b] * cr;
                        xr[b] = xr[a] - tr; xi[b] = xi[a] - ti;
                        xr[a] += tr; xi[a] += ti;
                        double next = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = next;
                    }
                }
            }
        }
    }

    public sealed class AudioDevice
    {
        public string Id;
        public string Name;
        public bool IsDefault;
    }

    // Lists active output or input devices and changes the default one.
    // Windows has no public API for the latter; IPolicyConfig is the interface
    // the Sound control panel itself uses (as do EarTrumpet and SoundSwitch).
    public static class AudioDevices
    {
        public static List<AudioDevice> List(bool capture)
        {
            var devices = new List<AudioDevice>();
            var enumerator = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumerator();
            try
            {
                string defaultId = null;
                CoreAudio.IMMDevice current;
                if (enumerator.GetDefaultAudioEndpoint(capture ? CoreAudio.Capture : CoreAudio.Render, CoreAudio.Console, out current) == 0 && current != null)
                {
                    current.GetId(out defaultId);
                    Marshal.ReleaseComObject(current);
                }

                CoreAudio.IMMDeviceCollection collection;
                if (enumerator.EnumAudioEndpoints(capture ? CoreAudio.Capture : CoreAudio.Render, CoreAudio.StateActive, out collection) != 0) return devices;
                try
                {
                    int count;
                    collection.GetCount(out count);
                    for (int i = 0; i < count; i++)
                    {
                        CoreAudio.IMMDevice device;
                        if (collection.Item(i, out device) != 0 || device == null) continue;
                        try
                        {
                            string id;
                            device.GetId(out id);
                            devices.Add(new AudioDevice { Id = id, Name = SystemAudio.ReadFriendlyName(device) ?? id, IsDefault = id == defaultId });
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(collection);
                }
            }
            catch (COMException ex)
            {
                Log.Error("list audio devices", ex);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
            devices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return devices;
        }

        // Makes the device the default (console and multimedia roles), exactly as
        // Windows' sound settings do. The separate communications device used by
        // call apps is left as the user configured it.
        public static bool SetDefault(string deviceId)
        {
            object client = null;
            try
            {
                client = new CoreAudio.PolicyConfigClient();
                var policy = (CoreAudio.IPolicyConfig)client;
                bool ok = policy.SetDefaultEndpoint(deviceId, CoreAudio.Console) == 0;
                ok &= policy.SetDefaultEndpoint(deviceId, CoreAudio.Multimedia) == 0;
                return ok;
            }
            catch (Exception ex)
            {
                Log.Error("set default audio device", ex);
                return false;
            }
            finally
            {
                if (client != null) Marshal.ReleaseComObject(client);
            }
        }
    }

    // Volume, mute and name of the default output (or input) device.
    // Re-acquired every few seconds so a change of default device is followed.
    public sealed class SystemAudio
    {
        private readonly bool capture;
        private CoreAudio.IAudioEndpointVolume volume;
        private DateTime acquiredAt;

        public SystemAudio(bool capture = false)
        {
            this.capture = capture;
        }

        public bool IsInput { get { return capture; } }

        // Friendly name of the current device, e.g. "Headphones (WH-1000XM4)".
        public string DeviceName { get; private set; }

        // Forces a re-read, e.g. right after the default device was changed.
        public void Invalidate()
        {
            acquiredAt = DateTime.MinValue;
        }

        // Null when unknown (no device).
        public bool? IsMuted()
        {
            try
            {
                Ensure();
                int muted;
                if (volume != null && volume.GetMute(out muted) == 0) return muted != 0;
            }
            catch (COMException)
            {
                Release();
            }
            return null;
        }

        // 0..1, or null when there is no output device.
        public double? Volume()
        {
            try
            {
                Ensure();
                float level;
                if (volume != null && volume.GetMasterVolumeLevelScalar(out level) == 0) return level;
            }
            catch (COMException)
            {
                Release();
            }
            return null;
        }

        // Raising the level also unmutes, like the volume keys do.
        public void SetVolume(double level)
        {
            try
            {
                Ensure();
                if (volume == null) return;
                level = Math.Max(0, Math.Min(1, level));
                volume.SetMasterVolumeLevelScalar((float)level, IntPtr.Zero);
                if (level > 0) volume.SetMute(0, IntPtr.Zero);
            }
            catch (COMException ex)
            {
                Log.Error("set volume", ex);
                Release();
            }
        }

        public void SetMuted(bool muted)
        {
            try
            {
                Ensure();
                if (volume != null) volume.SetMute(muted ? 1 : 0, IntPtr.Zero);
            }
            catch (COMException ex)
            {
                Log.Error("set mute", ex);
                Release();
            }
        }

        private void Ensure()
        {
            if (volume == null || (DateTime.UtcNow - acquiredAt).TotalSeconds > 5) Acquire();
        }

        private void Acquire()
        {
            Release();
            acquiredAt = DateTime.UtcNow;
            var enumerator = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumerator();
            try
            {
                CoreAudio.IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(capture ? CoreAudio.Capture : CoreAudio.Render, CoreAudio.Console, out device) != 0 || device == null)
                {
                    DeviceName = null;
                    return;
                }
                try
                {
                    Guid iid = typeof(CoreAudio.IAudioEndpointVolume).GUID;
                    object activated;
                    if (device.Activate(ref iid, CoreAudio.ClsctxAll, IntPtr.Zero, out activated) == 0)
                        volume = (CoreAudio.IAudioEndpointVolume)activated;
                    DeviceName = ReadFriendlyName(device);
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        internal static string ReadFriendlyName(CoreAudio.IMMDevice device)
        {
            CoreAudio.IPropertyStore store;
            if (device.OpenPropertyStore(0 /* STGM_READ */, out store) != 0 || store == null) return null;
            try
            {
                var key = new CoreAudio.PropertyKey { Fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 14 };
                var value = new CoreAudio.PropVariant();
                if (store.GetValue(ref key, out value) != 0) return null;
                try
                {
                    return value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null;
                }
                finally
                {
                    CoreAudio.PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        private void Release()
        {
            if (volume != null) Marshal.ReleaseComObject(volume);
            volume = null;
        }
    }

    internal static class CoreAudio
    {
        public const int Render = 0;
        public const int Capture = 1;
        public const int Console = 0;
        public const int Multimedia = 1;
        public const int StateActive = 1;
        public const int ClsctxAll = 23;
        public const int StreamFlagsLoopback = 0x00020000;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        public class MMDeviceEnumerator
        {
        }

        // Only the leading vtable slots that are called are declared.
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        }

        [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        public class PolicyConfigClient
        {
        }

        // Undocumented; only the slots up to SetDefaultEndpoint are called.
        [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey { public Guid Fmtid; public int Pid; }

        // PROPVARIANT: a 2-byte type, 6 reserved bytes, then a 16-byte union.
        [StructLayout(LayoutKind.Sequential)]
        public struct PropVariant
        {
            public ushort Type;
            public ushort Reserved1, Reserved2, Reserved3;
            public IntPtr Pointer;
            public IntPtr Pointer2;
        }

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant value);

        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr context);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, IntPtr context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute(int mute, IntPtr context);
            [PreserveSig] int GetMute(out int mute);
        }
    }
}
