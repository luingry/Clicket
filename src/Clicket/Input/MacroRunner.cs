using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Input;

/// <summary>
/// Plays one macro on its own dedicated thread: SendInput, precise waits, loops, and a cancel event checked at every wait.
/// Whatever happens, everything it pressed is released when it ends, so a stop never leaves a key or button stuck.
/// </summary>
internal sealed class MacroRunner
{
    /// <summary>Gap between the two clicks of a double click when the step holds each click for a while.</summary>
    private static readonly TimeSpan DoubleClickGap = TimeSpan.FromMilliseconds(45);

    private readonly Macro _macro;
    private readonly int _passes;
    private readonly int _startDelayMs;
    private readonly Modifiers _releaseFirst;
    private readonly ManualResetEvent _cancel = new ManualResetEvent(false);
    private readonly InputSender _sender = new InputSender();
    private readonly Random _random = new Random();
    private readonly Dictionary<Step, List<PathPoint>> _paths = new Dictionary<Step, List<PathPoint>>();
    private Thread? _thread;

    /// <param name="passes">How many times the whole list runs; 0 repeats until cancelled.</param>
    /// <param name="releaseFirst">Modifiers of the hotkey that may still be held; released so they do not combine with the first keys.</param>
    public MacroRunner(Macro macro, int passes, int startDelayMs, Modifiers releaseFirst)
    {
        _macro = macro;
        _passes = passes;
        _startDelayMs = startDelayMs;
        _releaseFirst = releaseFirst;
    }

    public string MacroId => _macro.Id;

    /// <summary>Raised on the runner thread when it ends, cancelled or not.</summary>
    public event Action<MacroRunner>? Finished;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Clicket run " + _macro.Name };
        _thread.Start();
    }

    public void Cancel() => _cancel.Set();

    public bool Join(int milliseconds) => _thread == null || _thread.Join(milliseconds);

    private void Run()
    {
        var started = Stopwatch.GetTimestamp();
        var passes = 0L;
        PreciseWaiter? stats = null;
        try
        {
            NativeMethods.UsePhysicalPixels();
            using var waiter = new PreciseWaiter(_cancel);
            stats = waiter;
            InputSender.TakeStats();
            if (_startDelayMs > 0 && !waiter.Wait(TimeSpan.FromMilliseconds(_startDelayMs))) return;
            ReleaseHeldModifiers();
            var program = MacroProgram.Build(_macro.Steps, out var error);
            if (error >= 0)
            {
                Log.Warn("Macro '" + _macro.Name + "' has an unmatched loop at step " + (error + 1) + "; not played.");
                return;
            }
            var oneMs = Stopwatch.Frequency / 1000;
            for (var pass = 0; _passes == 0 || pass < _passes; pass++)
            {
                var began = Stopwatch.GetTimestamp();
                if (!Execute(program, waiter)) return;
                passes++;
                // A repeating macro of instant steps would flood the input queue and spin a core: at most one pass per millisecond.
                if (program.Steps.Count == 0 && !waiter.Wait(TimeSpan.FromMilliseconds(50))) return;
                if (_passes != 1 && Stopwatch.GetTimestamp() - began < oneMs && !waiter.Wait(TimeSpan.FromMilliseconds(1))) return;
            }
        }
        catch (Exception e)
        {
            Log.Error("Macro '" + _macro.Name + "' failed.", e);
        }
        finally
        {
            try { _sender.ReleaseAll(); }
            catch (Exception e) { Log.Error("Release after run failed.", e); }
            LogRun(started, passes, stats);
            Finished?.Invoke(this);
        }
    }

    /// <summary>
    /// One line per run: rate, how long SendInput blocked (other apps' hooks delay every injected event) and how late the
    /// waits woke. When playback is slower than expected, this line says where the time went.
    /// </summary>
    private void LogRun(long started, long passes, PreciseWaiter? waiter)
    {
        try
        {
            var seconds = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
            var send = InputSender.TakeStats();
            double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            var line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Run '{0}': {1:0.00} s, {2} passes, {3} input events in {4} calls ({5:0.0}/s); SendInput avg {6:0.000} ms, max {7:0.000} ms",
                _macro.Name, seconds, passes, send.Events, send.Calls, seconds > 0 ? send.Events / seconds : 0,
                send.Calls > 0 ? Ms(send.Ticks) / send.Calls : 0, Ms(send.MaxTicks));
            if (waiter != null && waiter.Waits > 0)
                line += string.Format(System.Globalization.CultureInfo.InvariantCulture, "; waits {0}, late avg {1:0.000} ms, max {2:0.000} ms",
                    waiter.Waits, Ms(waiter.LateTicks) / waiter.Waits, Ms(waiter.MaxLateTicks));
            Log.Info(line);
        }
        catch (Exception e)
        {
            Log.Error("Run statistics failed.", e);
        }
    }

    private void ReleaseHeldModifiers()
    {
        if (_releaseFirst == Modifiers.None) return;
        var keys = new List<int>();
        if ((_releaseFirst & Modifiers.Ctrl) != 0) keys.AddRange(new[] { Vk.LControl, Vk.RControl });
        if ((_releaseFirst & Modifiers.Alt) != 0) keys.AddRange(new[] { Vk.LMenu, Vk.RMenu });
        if ((_releaseFirst & Modifiers.Shift) != 0) keys.AddRange(new[] { Vk.LShift, Vk.RShift });
        if ((_releaseFirst & Modifiers.Win) != 0) keys.AddRange(new[] { Vk.LWin, Vk.RWin });
        var released = false;
        foreach (var vk in keys)
        {
            if ((NativeMethods.GetAsyncKeyState(vk) & 0x8000) == 0) continue;
            if (!released && (_releaseFirst & (Modifiers.Alt | Modifiers.Win)) != 0) InputSender.MaskModifierRelease();
            _sender.Key(vk, 0, Vk.IsExtended(vk), up: true);
            released = true;
        }
    }

    /// <summary>One pass; false when cancelled.</summary>
    private bool Execute(MacroProgram program, PreciseWaiter waiter)
    {
        // (start index, repeats left after this one or -1 for forever, timestamp of the round start)
        var loops = new Stack<(int Start, int Remaining, long Began)>();
        for (var i = 0; i < program.Steps.Count; i++)
        {
            if (_cancel.WaitOne(0)) return false;
            var step = program.Steps[i];
            switch (step.Kind)
            {
                case StepKind.LoopStart:
                    loops.Push((i, step.Count <= 0 ? -1 : step.Count - 1, Stopwatch.GetTimestamp()));
                    break;
                case StepKind.LoopEnd:
                    if (loops.Count == 0) break;
                    var top = loops.Pop();
                    if (top.Remaining == 0) break;
                    // The loop's own pause between rounds (at least 1 ms), so even a loop of instant steps never floods input or spins a core.
                    if (!waiter.Wait(TimeSpan.FromMilliseconds(program.Steps[top.Start].LoopPauseMs))) return false;
                    loops.Push((top.Start, top.Remaining < 0 ? -1 : top.Remaining - 1, Stopwatch.GetTimestamp()));
                    i = top.Start;
                    break;
                default:
                    if (!Perform(step, waiter)) return false;
                    break;
            }
        }
        return true;
    }

    private bool Perform(Step step, PreciseWaiter waiter)
    {
        switch (step.Kind)
        {
            case StepKind.Key:
                switch (step.KeyAction)
                {
                    case KeyAction.Down: _sender.Key(step.Key, step.Scan, step.Extended, false); return true;
                    case KeyAction.Up: _sender.Key(step.Key, step.Scan, step.Extended, true); return true;
                    default:
                        if (step.Key == 0) return true;
                        var mods = ModifierKeys(step.Modifiers);
                        if (step.HoldMs == 0)
                        {
                            InputSender.KeyTap(step.Key, step.Scan, step.Extended, mods);
                            return true;
                        }
                        foreach (var mod in mods) _sender.Key(mod, 0, false, false);
                        _sender.Key(step.Key, step.Scan, step.Extended, false);
                        var held = waiter.Wait(TimeSpan.FromMilliseconds(step.HoldMs));
                        _sender.Key(step.Key, step.Scan, step.Extended, true);
                        for (var m = mods.Count - 1; m >= 0; m--) _sender.Key(mods[m], 0, false, true);
                        return held;
                }
            case StepKind.Mouse:
                return PerformMouse(step, waiter);
            case StepKind.Path:
                return PlayPath(step, waiter);
            case StepKind.Text:
                return TypeText(step, waiter);
            case StepKind.Delay:
                var ms = step.MsMax > step.Ms ? _random.Next(step.Ms, step.MsMax + 1) : step.Ms;
                return ms <= 0 || waiter.Wait(TimeSpan.FromMilliseconds(ms));
            case StepKind.Command:
                return RunCommand(step);
            case StepKind.Open:
                Open(step);
                return true;
            case StepKind.Script:
                return RunScript(step);
            default:
                return true;
        }
    }

    private bool PerformMouse(Step step, PreciseWaiter waiter)
    {
        switch (step.MouseAction)
        {
            case MouseAction.Down: _sender.Button(step.Button, false); return true;
            case MouseAction.Up: _sender.Button(step.Button, true); return true;
            case MouseAction.MoveTo: InputSender.MoveTo(step.X, step.Y); return true;
            case MouseAction.MoveBy:
                NativeMethods.GetCursorPos(out var cursor);
                InputSender.MoveTo(cursor.X + step.X, cursor.Y + step.Y);
                return true;
            case MouseAction.Scroll:
                InputSender.Wheel(step.Amount == 0 ? 120 : step.Amount, step.Horizontal);
                return true;
            case MouseAction.DoubleClick:
                if (step.HoldMs == 0)
                {
                    InputSender.ButtonTap(step.Button, 2);
                    return true;
                }
                if (!Click(step.Button, step.HoldMs, waiter)) return false;
                if (!waiter.Wait(DoubleClickGap)) return false;
                return Click(step.Button, step.HoldMs, waiter);
            default:
                if (step.HoldMs == 0)
                {
                    InputSender.ButtonTap(step.Button, 1);
                    return true;
                }
                return Click(step.Button, step.HoldMs, waiter);
        }
    }

    private bool Click(MouseButton button, int holdMs, PreciseWaiter waiter)
    {
        _sender.Button(button, false);
        var held = waiter.Wait(TimeSpan.FromMilliseconds(holdMs));
        _sender.Button(button, true);
        return held;
    }

    /// <summary>
    /// Replays every recorded sample on the original clock: each one is due at start + its own timestamp, so a late wake
    /// never shifts the rest. Samples that fell due together go out in a single SendInput, keeping the whole trajectory.
    /// </summary>
    private bool PlayPath(Step step, PreciseWaiter waiter)
    {
        if (!_paths.TryGetValue(step, out var points))
        {
            points = PathCodec.Decode(step.PathData);
            _paths[step] = points;
        }
        if (points.Count == 0) return true;

        int offsetX = 0, offsetY = 0;
        if (step.Relative)
        {
            NativeMethods.GetCursorPos(out var cursor);
            offsetX = cursor.X - points[0].X;
            offsetY = cursor.Y - points[0].Y;
        }

        var start = Stopwatch.GetTimestamp();
        var ticksPerMicro = Stopwatch.Frequency / 1_000_000.0;
        var batch = new List<(int X, int Y)>(16);
        var i = 0;
        while (i < points.Count)
        {
            var due = start + (long)(points[i].Micros * ticksPerMicro);
            if (!waiter.WaitUntil(due)) return false;
            var now = Stopwatch.GetTimestamp();
            batch.Clear();
            while (i < points.Count && start + (long)(points[i].Micros * ticksPerMicro) <= now)
            {
                batch.Add((points[i].X + offsetX, points[i].Y + offsetY));
                i++;
            }
            if (batch.Count == 0) // the waiter returned on time to the tick; take the due point
            {
                batch.Add((points[i].X + offsetX, points[i].Y + offsetY));
                i++;
            }
            InputSender.MoveTo(batch);
        }
        return true;
    }

    private bool TypeText(Step step, PreciseWaiter waiter)
    {
        var text = step.Text ?? "";
        for (var i = 0; i < text.Length; i++)
        {
            if (_cancel.WaitOne(0)) return false;
            var c = text[i];
            if (c == '\r') continue;
            if (c == '\n') TapKey(Vk.Return);
            else if (c == '\t') TapKey(Vk.Tab);
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                InputSender.Unicode(new[] { c, text[i + 1] });
                i++;
            }
            else InputSender.Unicode(new[] { c });
            if (step.CharDelay > 0 && !waiter.Wait(TimeSpan.FromMilliseconds(step.CharDelay))) return false;
        }
        return true;
    }

    private void TapKey(int vk)
    {
        _sender.Key(vk, 0, false, false);
        _sender.Key(vk, 0, false, true);
    }

    private bool RunCommand(Step step)
    {
        var command = step.Command ?? "";
        if (command.Trim().Length == 0) return true;
        var info = step.Shell == ShellKind.PowerShell
            ? new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + Quote(command))
            : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c " + command);
        info.UseShellExecute = false;
        info.CreateNoWindow = step.Hidden;
        info.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            using var process = Process.Start(info);
            if (process == null || !step.Wait) return true;
            using var exited = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, ownsHandle: false) };
            return WaitHandle.WaitAny(new WaitHandle[] { _cancel, exited }) == 1;
        }
        catch (Exception e) when (e is Win32Exception || e is InvalidOperationException || e is FileNotFoundException)
        {
            Log.Warn("Command failed in macro '" + _macro.Name + "': " + e.GetType().Name + " " + e.Message);
            return true;
        }
    }

    /// <summary>
    /// Writes the script to a temp .ps1 (UTF-8 with BOM, which Windows PowerShell 5.1 needs for non-ASCII text) and runs it
    /// with the editor's values as named arguments. The file is removed once PowerShell exits.
    /// </summary>
    private bool RunScript(Step step)
    {
        var source = step.Script ?? "";
        if (source.Trim().Length == 0) return true;
        string path;
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "Clicket");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, "script-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(path, source, new System.Text.UTF8Encoding(true));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Log.Warn("Script could not be written in macro '" + _macro.Name + "': " + e.GetType().Name);
            return true;
        }

        var invocation = ScriptParams.BuildInvocation(path, ScriptParams.Parse(source), step.ScriptValues());
        var info = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + invocation.Replace("\"", "\\\"") + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = step.Hidden,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        try
        {
            var process = new Process { StartInfo = info };
            if (step.Wait)
            {
                // Waiting: the runner owns the process; it waits, then cleans up itself.
                using (process)
                {
                    process.Start();
                    using var exited = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, ownsHandle: false) };
                    var finished = WaitHandle.WaitAny(new WaitHandle[] { _cancel, exited }) == 1;
                    if (finished) TryDelete(path);
                    else DeleteWhenDone(process.Id, path);
                    return finished;
                }
            }
            // Not waiting: the handler exists before events are enabled, so a script that ends at once still cleans up.
            process.Exited += (s, e) =>
            {
                TryDelete(path);
                process.Dispose();
            };
            process.EnableRaisingEvents = true;
            process.Start();
            return true;
        }
        catch (Exception e) when (e is Win32Exception || e is InvalidOperationException || e is FileNotFoundException)
        {
            Log.Warn("Script failed in macro '" + _macro.Name + "': " + e.GetType().Name + " " + e.Message);
            TryDelete(path);
            return true;
        }
    }

    /// <summary>A stop leaves the script running; its file goes once it exits.</summary>
    private static void DeleteWhenDone(int processId, string path)
    {
        try
        {
            var other = Process.GetProcessById(processId);
            other.Exited += (s, e) =>
            {
                TryDelete(path);
                other.Dispose();
            };
            other.EnableRaisingEvents = true;
            if (other.HasExited) TryDelete(path);
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is Win32Exception)
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    private void Open(Step step)
    {
        var target = (step.Target ?? "").Trim().Trim('"');
        if (target.Length == 0) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target), step.Arguments ?? "") { UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception || e is InvalidOperationException || e is FileNotFoundException)
        {
            Log.Warn("Open failed in macro '" + _macro.Name + "': " + e.GetType().Name + " " + e.Message);
        }
    }

    private static List<int> ModifierKeys(Modifiers modifiers)
    {
        var keys = new List<int>(4);
        if ((modifiers & Modifiers.Ctrl) != 0) keys.Add(Vk.LControl);
        if ((modifiers & Modifiers.Alt) != 0) keys.Add(Vk.LMenu);
        if ((modifiers & Modifiers.Shift) != 0) keys.Add(Vk.LShift);
        if ((modifiers & Modifiers.Win) != 0) keys.Add(Vk.LWin);
        return keys;
    }

    private static string Quote(string command) => "\"" + command.Replace("\"", "\\\"") + "\"";
}
