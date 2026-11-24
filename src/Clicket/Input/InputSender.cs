using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Input;

/// <summary>
/// SendInput with Clicket's signature in dwExtraInfo, so the hooks ignore what Clicket itself plays
/// (a macro can never trigger a macro or end up in a recording). Tracks what it holds down so a stop releases it.
/// </summary>
internal sealed class InputSender
{
    /// <summary>"CLKT".</summary>
    public static readonly UIntPtr Signature = new UIntPtr(0x434C4B54);
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.Input>();

    private readonly HashSet<int> _heldKeys = new HashSet<int>();
    private readonly Dictionary<int, (int Scan, bool Extended)> _heldKeyInfo = new Dictionary<int, (int, bool)>();
    private readonly HashSet<MouseButton> _heldButtons = new HashSet<MouseButton>();

    public void Key(int vk, int scan, bool extended, bool up)
    {
        Send(KeyEvent(vk, ref scan, ref extended, up));
        if (up)
        {
            _heldKeys.Remove(vk);
            _heldKeyInfo.Remove(vk);
        }
        else if (_heldKeys.Add(vk))
        {
            _heldKeyInfo[vk] = (scan, extended);
        }
    }

    /// <summary>Types one UTF-16 unit; surrogate halves are sent as a pair by the caller.</summary>
    public static void Unicode(char[] units)
    {
        var inputs = new NativeMethods.Input[units.Length * 2];
        for (var i = 0; i < units.Length; i++)
        {
            inputs[i] = KeyInput(0, units[i], NativeMethods.KeyeventfUnicode);
            inputs[units.Length + i] = KeyInput(0, units[i], NativeMethods.KeyeventfUnicode | NativeMethods.KeyeventfKeyUp);
        }
        Send(inputs);
    }

    public void Button(MouseButton button, bool up)
    {
        Send(ButtonEvent(button, up));
        if (up) _heldButtons.Remove(button);
        else _heldButtons.Add(button);
    }

    /// <summary>
    /// Instant clicks, as AutoHotkey sends them: every down and up in a single SendInput call, so nothing else can
    /// interleave and nothing is ever left held. <paramref name="count"/> 2 is a double click.
    /// </summary>
    public static void ButtonTap(MouseButton button, int count)
    {
        var inputs = new NativeMethods.Input[count * 2];
        for (var i = 0; i < count; i++)
        {
            inputs[i * 2] = ButtonEvent(button, false);
            inputs[i * 2 + 1] = ButtonEvent(button, true);
        }
        Send(inputs);
    }

    /// <summary>An instant key press with its modifiers, in one call: modifiers down, key down, key up, modifiers up.</summary>
    public static void KeyTap(int vk, int scan, bool extended, IReadOnlyList<int> modifiers)
    {
        var inputs = new NativeMethods.Input[2 + modifiers.Count * 2];
        var n = 0;
        foreach (var mod in modifiers)
        {
            int s = 0;
            var e = false;
            inputs[n++] = KeyEvent(mod, ref s, ref e, false);
        }
        int keyScan = scan;
        var keyExtended = extended;
        inputs[n++] = KeyEvent(vk, ref keyScan, ref keyExtended, false);
        inputs[n++] = KeyEvent(vk, ref keyScan, ref keyExtended, true);
        for (var m = modifiers.Count - 1; m >= 0; m--)
        {
            int s = 0;
            var e = false;
            inputs[n++] = KeyEvent(modifiers[m], ref s, ref e, true);
        }
        Send(inputs);
    }

    /// <summary>
    /// A recorded key carries its scan code and extended flag (numpad keys with NumLock off differ from the navigation
    /// block only by that flag); a key built in the editor has no scan code and takes the usual flag.
    /// </summary>
    private static NativeMethods.Input KeyEvent(int vk, ref int scan, ref bool extended, bool up)
    {
        if (scan == 0)
        {
            scan = (int)NativeMethods.MapVirtualKey((uint)vk, 0); // MAPVK_VK_TO_VSC
            extended = extended || Vk.IsExtended(vk);
        }
        var flags = (up ? NativeMethods.KeyeventfKeyUp : 0) | (extended ? NativeMethods.KeyeventfExtendedKey : 0);
        return KeyInput((ushort)vk, (ushort)scan, flags);
    }

    private static NativeMethods.Input ButtonEvent(MouseButton button, bool up)
    {
        uint flags, data = 0;
        switch (button)
        {
            case MouseButton.Right: flags = up ? NativeMethods.MouseeventfRightUp : NativeMethods.MouseeventfRightDown; break;
            case MouseButton.Middle: flags = up ? NativeMethods.MouseeventfMiddleUp : NativeMethods.MouseeventfMiddleDown; break;
            case MouseButton.X1: flags = up ? NativeMethods.MouseeventfXUp : NativeMethods.MouseeventfXDown; data = 1; break;
            case MouseButton.X2: flags = up ? NativeMethods.MouseeventfXUp : NativeMethods.MouseeventfXDown; data = 2; break;
            default: flags = up ? NativeMethods.MouseeventfLeftUp : NativeMethods.MouseeventfLeftDown; break;
        }
        return MouseInput(0, 0, data, flags);
    }

    public static void Wheel(int delta, bool horizontal) =>
        Send(MouseInput(0, 0, unchecked((uint)delta), horizontal ? NativeMethods.MouseeventfHWheel : NativeMethods.MouseeventfWheel));

    /// <summary>Moves to physical screen pixels on the virtual desktop, exactly: the normalized value is chosen so Windows' floor lands on the pixel.</summary>
    public static void MoveTo(int x, int y) => Send(MoveInput(x, y, VirtualScreen.Current()));

    /// <summary>Several absolute moves in one call (path samples that fell due together), keeping every point of the trajectory.</summary>
    public static void MoveTo(IReadOnlyList<(int X, int Y)> points)
    {
        if (points.Count == 0) return;
        var screen = VirtualScreen.Current();
        var inputs = new NativeMethods.Input[points.Count];
        for (var i = 0; i < points.Count; i++) inputs[i] = MoveInput(points[i].X, points[i].Y, screen);
        Send(inputs);
    }

    /// <summary>Releases everything this sender still holds; called whenever a run ends, normally or not.</summary>
    public void ReleaseAll()
    {
        foreach (var vk in new List<int>(_heldKeys))
        {
            var info = _heldKeyInfo.TryGetValue(vk, out var held) ? held : (0, false);
            Key(vk, info.Item1, info.Item2, up: true);
        }
        foreach (var button in new List<MouseButton>(_heldButtons)) Button(button, up: true);
    }

    /// <summary>
    /// An unassigned key tap (vk 0xE8), as AutoHotkey does: after a suppressed hotkey that used Alt or Win,
    /// releasing the modifier would otherwise open the menu bar or the Start menu.
    /// </summary>
    public static void MaskModifierRelease()
    {
        Send(new[] { KeyInput(0xE8, 0, 0), KeyInput(0xE8, 0, NativeMethods.KeyeventfKeyUp) });
    }

    private static NativeMethods.Input MoveInput(int x, int y, VirtualScreen screen)
    {
        var nx = (int)(((long)(x - screen.Left) * 65536 + screen.Width - 1) / Math.Max(1, screen.Width));
        var ny = (int)(((long)(y - screen.Top) * 65536 + screen.Height - 1) / Math.Max(1, screen.Height));
        nx = Math.Max(0, Math.Min(65535, nx));
        ny = Math.Max(0, Math.Min(65535, ny));
        return MouseInput(nx, ny, 0, NativeMethods.MouseeventfMove | NativeMethods.MouseeventfAbsolute | NativeMethods.MouseeventfVirtualDesk);
    }

    private static NativeMethods.Input KeyInput(ushort vk, ushort scan, uint flags) => new NativeMethods.Input
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeybdInput { Vk = vk, Scan = scan, Flags = flags, ExtraInfo = Signature } },
    };

    private static NativeMethods.Input MouseInput(int dx, int dy, uint data, uint flags) => new NativeMethods.Input
    {
        Type = NativeMethods.InputMouse,
        Data = new NativeMethods.InputUnion { Mouse = new NativeMethods.MouseInput { Dx = dx, Dy = dy, MouseData = data, Flags = flags, ExtraInfo = Signature } },
    };

    // Per runner thread: how many calls, events and how long SendInput blocked (it waits for every low-level hook in
    // the system, so a slow third-party hook shows up here). Logged when a run ends.
    [ThreadStatic] private static long _calls, _events, _ticks, _maxTicks;

    public static (long Calls, long Events, long Ticks, long MaxTicks) TakeStats()
    {
        var stats = (_calls, _events, _ticks, _maxTicks);
        _calls = _events = _ticks = _maxTicks = 0;
        return stats;
    }

    private static void Send(params NativeMethods.Input[] inputs)
    {
        if (inputs.Length == 0) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
        var took = System.Diagnostics.Stopwatch.GetTimestamp() - started;
        _calls++;
        _events += inputs.Length;
        _ticks += took;
        if (took > _maxTicks) _maxTicks = took;
        if (sent != inputs.Length) Log.Warn("SendInput delivered " + sent + " of " + inputs.Length + " (error " + Marshal.GetLastWin32Error() + "); input may be blocked by a higher-integrity window.");
    }

    private readonly struct VirtualScreen
    {
        private VirtualScreen(int left, int top, int width, int height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public int Left { get; }
        public int Top { get; }
        public int Width { get; }
        public int Height { get; }

        public static VirtualScreen Current() => new VirtualScreen(
            NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen), NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen), NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));
    }
}
