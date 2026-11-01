using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Islet
{
    internal static class MediaKeys
    {
        // The system play/pause media key, as a keyboard's media button sends it.
        public static void PlayPause()
        {
            const byte VkMediaPlayPause = 0xB3;
            const uint ExtendedKey = 0x1, KeyUp = 0x2;
            Native.keybd_event(VkMediaPlayPause, 0, ExtendedKey, UIntPtr.Zero);
            Native.keybd_event(VkMediaPlayPause, 0, ExtendedKey | KeyUp, UIntPtr.Zero);
        }
    }

    internal static class Native
    {
        public const int GwlExStyle = -20;
        public const int WsExTransparent = 0x00000020;
        public const int WsExToolWindow = 0x00000080;
        public const int WsExNoActivate = 0x08000000;
        public const uint MonitorDefaultToNearest = 2;
        public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
        public static readonly IntPtr HwndTopmost = new IntPtr(-1);
        public const uint WdaExcludeFromCapture = 0x00000011;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect32 { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point32 { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo { public int Size; public Rect32 Monitor; public Rect32 Work; public uint Flags; }

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point32 point);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        public const int WmHotkey = 0x0312;
        public const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // GDI capture of the screen behind the island.
        public const int Halftone = 4;
        public const uint SrcCopy = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
        [DllImport("gdi32.dll")]
        public static extern bool StretchBlt(IntPtr dest, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }
}
