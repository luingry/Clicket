using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Input;

internal enum HookEventKind { Raw, TriggerDown, TriggerUp }

internal struct HookEvent
{
    public HookEventKind Kind;
    public RawEvent Raw;
    public Hotkey Hotkey;
    public int TriggerKey;
}

/// <summary>
/// Low-level keyboard and mouse hooks on one dedicated, high-priority thread that sleeps in GetMessage.
/// The callbacks only read the event, check a frozen trigger set and enqueue: Windows silently drops slow hooks.
/// The mouse hook exists only while something needs it (recording, or a macro on a mouse button), because every
/// pointer move passes through it.
/// </summary>
internal sealed class InputHook : IDisposable
{
    private const int ApplyMessage = NativeMethods.WmApp + 1;
    private static readonly int KeyExtraOffset = (int)Marshal.OffsetOf<NativeMethods.KbdLlHookStruct>(nameof(NativeMethods.KbdLlHookStruct.ExtraInfo));
    private static readonly int MouseExtraOffset = (int)Marshal.OffsetOf<NativeMethods.MsLlHookStruct>(nameof(NativeMethods.MsLlHookStruct.ExtraInfo));

    private readonly ConcurrentQueue<HookEvent> _queue;
    private readonly AutoResetEvent _signal;
    private readonly NativeMethods.HookProc _keyboardProc;
    private readonly NativeMethods.HookProc _mouseProc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
    private readonly bool[] _suppressed = new bool[256];
    private readonly bool[] _modifierDown = new bool[256];
    private readonly long[] _lastDown = new long[256];
    private static readonly long LostReleaseTicks = Stopwatch.Frequency * 3 / 2;
    private uint _threadId;
    private IntPtr _keyboardHook, _mouseHook;
    private volatile HashSet<Hotkey> _triggers = new HashSet<Hotkey>();
    private volatile bool _wantKeyboard, _wantMouse, _forwardKeys, _forwardMouse;
    private Modifiers _modifiers;

    public InputHook(ConcurrentQueue<HookEvent> queue, AutoResetEvent signal)
    {
        _queue = queue;
        _signal = signal;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        RuntimeHelpers.PrepareDelegate(_keyboardProc);
        RuntimeHelpers.PrepareDelegate(_mouseProc);
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Clicket hooks" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Hotkeys to swallow and report. The set is replaced, never mutated, so the callback reads it without a lock.</summary>
    public void SetTriggers(HashSet<Hotkey> triggers)
    {
        var mouse = false;
        foreach (var hotkey in triggers) mouse |= hotkey.IsMouse;
        _triggersHaveMouse = mouse;
        _triggers = triggers;
    }

    private volatile bool _triggersHaveMouse;

    /// <summary>Which hooks exist and what reaches the queue. Applied on the hook thread.</summary>
    public void Configure(bool keyboard, bool mouse, bool forwardKeys, bool forwardMouse)
    {
        _wantKeyboard = keyboard;
        _wantMouse = mouse;
        _forwardKeys = forwardKeys;
        _forwardMouse = forwardMouse;
        NativeMethods.PostThreadMessage(_threadId, ApplyMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
    }

    private void Run()
    {
        try
        {
            NativeMethods.UsePhysicalPixels();
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // creates the message queue before anyone posts
            _threadId = NativeMethods.GetCurrentThreadId();
            _ready.Set();
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message == ApplyMessage) Apply();
            }
        }
        catch (Exception e)
        {
            Log.Error("Hook thread failed.", e);
        }
        finally
        {
            Unhook(ref _keyboardHook);
            Unhook(ref _mouseHook);
            _ready.Set();
        }
    }

    private void Apply()
    {
        var module = NativeMethods.GetModuleHandle(null);
        if (_wantKeyboard && _keyboardHook == IntPtr.Zero)
        {
            SeedModifiers();
            _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProc, module, 0);
            if (_keyboardHook == IntPtr.Zero) Log.Error("Keyboard hook failed (error " + Marshal.GetLastWin32Error() + ").");
            else Log.Info("Keyboard hook installed.");
        }
        else if (!_wantKeyboard && _keyboardHook != IntPtr.Zero)
        {
            Unhook(ref _keyboardHook);
            Log.Info("Keyboard hook removed.");
        }

        if (_wantMouse && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero) Log.Error("Mouse hook failed (error " + Marshal.GetLastWin32Error() + ").");
            else Log.Info("Mouse hook installed.");
        }
        else if (!_wantMouse && _mouseHook != IntPtr.Zero)
        {
            Unhook(ref _mouseHook);
            Log.Info("Mouse hook removed.");
        }
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    /// <summary>Modifiers already held when the hook starts would otherwise read as released.</summary>
    private void SeedModifiers()
    {
        foreach (var vk in new[] { Vk.LShift, Vk.RShift, Vk.LControl, Vk.RControl, Vk.LMenu, Vk.RMenu, Vk.LWin, Vk.RWin })
            _modifierDown[vk] = (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        RecomputeModifiers();
    }

    private void RecomputeModifiers()
    {
        var m = Modifiers.None;
        if (_modifierDown[Vk.LControl] || _modifierDown[Vk.RControl] || _modifierDown[Vk.Control]) m |= Modifiers.Ctrl;
        if (_modifierDown[Vk.LMenu] || _modifierDown[Vk.RMenu] || _modifierDown[Vk.Menu]) m |= Modifiers.Alt;
        if (_modifierDown[Vk.LShift] || _modifierDown[Vk.RShift] || _modifierDown[Vk.Shift]) m |= Modifiers.Shift;
        if (_modifierDown[Vk.LWin] || _modifierDown[Vk.RWin]) m |= Modifiers.Win;
        _modifiers = m;
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != NativeMethods.HcAction || Marshal.ReadIntPtr(lParam, KeyExtraOffset) == (IntPtr)(long)InputSender.Signature.ToUInt64())
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        var now = Stopwatch.GetTimestamp();
        var vk = Marshal.ReadInt32(lParam, 0) & 0xFF;
        var scan = Marshal.ReadInt32(lParam, 4);
        var flags = (uint)Marshal.ReadInt32(lParam, 8);
        var message = wParam.ToInt32();
        var down = message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown;

        if (Vk.IsModifier(vk))
        {
            _modifierDown[vk] = down;
            RecomputeModifiers();
        }

        var suppress = false;
        if (down && !Vk.IsModifier(vk))
        {
            // The secure desktop (Ctrl+Alt+Del, Win+L, UAC) hides key-ups from every hook: re-read the modifiers before matching,
            // and a "repeat" of a swallowed key that Windows says is up is really a new press after a lost release.
            SeedModifiers();
            // A swallowed key never reaches Windows' key state, so the release can't be asked of GetAsyncKeyState
            // (that made every auto-repeat of a held hotkey look like a new press). Auto-repeat arrives every ~33 ms
            // after at most a 1 s delay, so a "repeat" after a longer silence is a new press whose release was lost.
            if (_suppressed[vk] && now - _lastDown[vk] > LostReleaseTicks)
            {
                _suppressed[vk] = false;
                Push(new HookEvent { Kind = HookEventKind.TriggerUp, TriggerKey = vk });
            }
            _lastDown[vk] = now;
        }
        if (down)
        {
            if (_suppressed[vk])
            {
                suppress = true; // auto-repeat of a swallowed hotkey
            }
            else if (!Vk.IsModifier(vk))
            {
                var hotkey = new Hotkey(vk, _modifiers);
                if (_triggers.Contains(hotkey))
                {
                    _suppressed[vk] = true;
                    suppress = true;
                    Push(new HookEvent { Kind = HookEventKind.TriggerDown, Hotkey = hotkey, TriggerKey = vk });
                }
            }
        }
        else if (_suppressed[vk])
        {
            _suppressed[vk] = false;
            suppress = true;
            Push(new HookEvent { Kind = HookEventKind.TriggerUp, TriggerKey = vk });
        }

        if (!suppress && _forwardKeys)
        {
            Push(new HookEvent
            {
                Kind = HookEventKind.Raw,
                Raw = new RawEvent { Kind = down ? RawKind.KeyDown : RawKind.KeyUp, Vk = vk, Scan = scan, Extended = (flags & NativeMethods.LlkhfExtended) != 0, Micros = now },
            });
        }
        return suppress ? new IntPtr(1) : NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != NativeMethods.HcAction || Marshal.ReadIntPtr(lParam, MouseExtraOffset) == (IntPtr)(long)InputSender.Signature.ToUInt64())
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);

        var now = Stopwatch.GetTimestamp();
        var message = wParam.ToInt32();
        var x = Marshal.ReadInt32(lParam, 0);
        var y = Marshal.ReadInt32(lParam, 4);
        var data = (uint)Marshal.ReadInt32(lParam, 8);

        if (message == NativeMethods.WmMouseMove)
        {
            if (_forwardMouse) Push(new HookEvent { Kind = HookEventKind.Raw, Raw = new RawEvent { Kind = RawKind.Move, X = x, Y = y, Micros = now } });
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        RawKind kind;
        var button = MouseButton.Left;
        var delta = 0;
        switch (message)
        {
            case NativeMethods.WmLButtonDown: kind = RawKind.ButtonDown; break;
            case NativeMethods.WmLButtonUp: kind = RawKind.ButtonUp; break;
            case NativeMethods.WmRButtonDown: kind = RawKind.ButtonDown; button = MouseButton.Right; break;
            case NativeMethods.WmRButtonUp: kind = RawKind.ButtonUp; button = MouseButton.Right; break;
            case NativeMethods.WmMButtonDown: kind = RawKind.ButtonDown; button = MouseButton.Middle; break;
            case NativeMethods.WmMButtonUp: kind = RawKind.ButtonUp; button = MouseButton.Middle; break;
            case NativeMethods.WmXButtonDown: kind = RawKind.ButtonDown; button = (data >> 16) == 2 ? MouseButton.X2 : MouseButton.X1; break;
            case NativeMethods.WmXButtonUp: kind = RawKind.ButtonUp; button = (data >> 16) == 2 ? MouseButton.X2 : MouseButton.X1; break;
            case NativeMethods.WmMouseWheel: kind = RawKind.Wheel; delta = (short)(data >> 16); break;
            case NativeMethods.WmMouseHWheel: kind = RawKind.HWheel; delta = (short)(data >> 16); break;
            default: return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        var suppress = false;
        var triggerVk = button == MouseButton.Middle ? Vk.MButton : button == MouseButton.X1 ? Vk.XButton1 : button == MouseButton.X2 ? Vk.XButton2 : 0;
        if (triggerVk != 0)
        {
            if (kind == RawKind.ButtonDown)
            {
                if (_triggersHaveMouse) SeedModifiers();
                if (_suppressed[triggerVk])
                {
                    // A new press of a swallowed button: its release was lost (secure desktop), so close the old one first.
                    _suppressed[triggerVk] = false;
                    Push(new HookEvent { Kind = HookEventKind.TriggerUp, TriggerKey = triggerVk });
                }
                var hotkey = new Hotkey(triggerVk, _modifiers);
                if (_triggers.Contains(hotkey))
                {
                    _suppressed[triggerVk] = true;
                    suppress = true;
                    Push(new HookEvent { Kind = HookEventKind.TriggerDown, Hotkey = hotkey, TriggerKey = triggerVk });
                }
            }
            else if (kind == RawKind.ButtonUp && _suppressed[triggerVk])
            {
                _suppressed[triggerVk] = false;
                suppress = true;
                Push(new HookEvent { Kind = HookEventKind.TriggerUp, TriggerKey = triggerVk });
            }
        }

        if (!suppress && _forwardMouse)
        {
            Push(new HookEvent { Kind = HookEventKind.Raw, Raw = new RawEvent { Kind = kind, Button = button, X = x, Y = y, WheelDelta = delta, Micros = now } });
        }
        return suppress ? new IntPtr(1) : NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void Push(HookEvent e)
    {
        _queue.Enqueue(e);
        _signal.Set();
    }
}
