using System;
using System.Runtime.InteropServices;

namespace Clicket.Native;

internal static class NativeMethods
{
    public const int WmClose = 0x0010, WmContextMenu = 0x007B, WmApp = 0x8000, NinSelect = 0x0400, NinKeySelect = 0x0401;
    public const int WmLButtonDblClk = 0x0203, WmQuit = 0x0012, WmNcHitTest = 0x0084;
    public const int WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000, WsExTopmost = 0x00000008, WsPopup = unchecked((int)0x80000000);
    public const int GwlExStyle = -20;
    public const int SmCxSmIcon = 49, AsfwAny = -1, MdtEffectiveDpi = 0;
    public const uint SwpPlace = 0x0004 | 0x0010; // SWP_NOZORDER | SWP_NOACTIVATE
    public const uint SwpMoveOnly = SwpPlace | 0x0001; // SWP_NOSIZE
    public const uint MonitorDefaultToNearest = 2, NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    public const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NotifyIconVersion4 = 4;
    public const double BaseDpi = 96;

    // Hooks
    public const int WhKeyboardLl = 13, WhMouseLl = 14, HcAction = 0;
    public const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    public const int WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonDown = 0x0204, WmRButtonUp = 0x0205;
    public const int WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmMouseWheel = 0x020A, WmXButtonDown = 0x020B, WmXButtonUp = 0x020C, WmMouseHWheel = 0x020E;
    public const uint LlkhfExtended = 0x01, LlkhfInjected = 0x10, LlmhfInjected = 0x01;

    // SendInput
    public const uint InputMouse = 0, InputKeyboard = 1;
    public const uint KeyeventfExtendedKey = 0x0001, KeyeventfKeyUp = 0x0002, KeyeventfUnicode = 0x0004, KeyeventfScanCode = 0x0008;
    public const uint MouseeventfMove = 0x0001, MouseeventfLeftDown = 0x0002, MouseeventfLeftUp = 0x0004, MouseeventfRightDown = 0x0008, MouseeventfRightUp = 0x0010;
    public const uint MouseeventfMiddleDown = 0x0020, MouseeventfMiddleUp = 0x0040, MouseeventfXDown = 0x0080, MouseeventfXUp = 0x0100;
    public const uint MouseeventfWheel = 0x0800, MouseeventfHWheel = 0x1000, MouseeventfVirtualDesk = 0x4000, MouseeventfAbsolute = 0x8000;
    public const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    // Waitable timer
    public const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id, Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Frame, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public uint VkCode, ScanCode, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MsLlHookStruct
    {
        public Point Pt;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Window;
        public int Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeybdInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeybdInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWaitableTimerExW")]
    public static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessage(out Msg msg, IntPtr window, int min, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out Msg msg, IntPtr window, int min, int max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetKeyNameText(int lParam, System.Text.StringBuilder buffer, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[]? bits);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr handle);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] public static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>The work area of the monitor nearest the point, in physical pixels, and that monitor's scale.</summary>
    public static Rect WorkAreaAt(Point point, out double scale)
    {
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _);
        scale = dpi / BaseDpi;
        return info.Work;
    }

    /// <summary>Per-monitor-v2 for the calling thread, so hook points and SendInput share physical pixels.</summary>
    public static void UsePhysicalPixels()
    {
        try { SetThreadDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Rounded corners and a dark frame on Windows 11; harmless where unsupported.</summary>
    public static void StyleFrame(IntPtr window)
    {
        try
        {
            var dark = 1;
            DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));
            var round = 2;
            DwmSetWindowAttribute(window, 33, ref round, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { }
    }
}
