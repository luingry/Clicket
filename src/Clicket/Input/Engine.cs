using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Input;

/// <summary>
/// The input core: owns the hooks, maps hotkeys to macros, starts and stops runners, and records.
/// Hook events are handled on one dispatch thread that sleeps on an event while nothing happens.
/// Public members are thread-safe; events are raised on background threads.
/// </summary>
internal sealed class Engine : IDisposable
{
    private readonly ConcurrentQueue<HookEvent> _queue = new ConcurrentQueue<HookEvent>();
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);
    private readonly InputHook _hook;
    private readonly Thread _dispatcher;
    private readonly object _gate = new object();
    private readonly object _drainGate = new object();
    private readonly Dictionary<string, MacroRunner> _runners = new Dictionary<string, MacroRunner>();
    private readonly Dictionary<int, string> _heldTriggers = new Dictionary<int, string>();
    private readonly uint _ownProcess = (uint)Process.GetCurrentProcess().Id;
    private Dictionary<Hotkey, Macro> _byHotkey = new Dictionary<Hotkey, Macro>();
    private Hotkey _stopHotkey, _recordHotkey;
    private RecordingBuilder? _recorder;
    private long _recordStart;
    private volatile bool _disposed;
    private bool _suspended;

    public Engine()
    {
        _hook = new InputHook(_queue, _signal);
        _dispatcher = new Thread(Dispatch) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Clicket dispatch" };
        _dispatcher.Start();
    }

    /// <summary>A macro started (true) or ended (false).</summary>
    public event Action<string, bool>? RunStateChanged;

    /// <summary>The record hotkey was pressed; the UI decides whether that starts or stops a recording.</summary>
    public event Action? RecordHotkeyPressed;

    public bool IsRecording
    {
        get { lock (_gate) return _recorder != null; }
    }

    public int RecordedCount
    {
        get { lock (_drainGate) return _recorder?.Count ?? 0; }
    }

    public bool IsRunning(string id)
    {
        lock (_gate) return _runners.ContainsKey(id);
    }

    /// <summary>Replaces the hotkey table with copies of the given macros, so later edits never race a running macro.</summary>
    public void Configure(IEnumerable<Macro> macros, Hotkey stopHotkey, Hotkey recordHotkey)
    {
        var map = new Dictionary<Hotkey, Macro>();
        foreach (var macro in macros)
        {
            if (!macro.Enabled || macro.Hotkey.IsEmpty || macro.Hotkey == stopHotkey || macro.Hotkey == recordHotkey) continue;
            if (!map.ContainsKey(macro.Hotkey)) map[macro.Hotkey] = macro.Clone();
        }
        lock (_gate)
        {
            _byHotkey = map;
            _stopHotkey = stopHotkey;
            _recordHotkey = recordHotkey;
            ApplyHooks();
        }
    }

    /// <summary>While a hotkey or key field is listening, no hotkey is swallowed, so the field can capture any combination.</summary>
    public void SuspendTriggers(bool suspend)
    {
        lock (_gate)
        {
            if (_suspended == suspend) return;
            _suspended = suspend;
            ApplyHooks();
        }
    }

    /// <summary>Plays a macro now (or after a countdown), unless it is already running.</summary>
    public void Play(Macro macro, int startDelayMs, int passes)
    {
        Start(macro.Clone(), passes, startDelayMs, Modifiers.None);
    }

    public void Stop(string id)
    {
        lock (_gate)
        {
            if (_runners.TryGetValue(id, out var runner)) runner.Cancel();
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var runner in _runners.Values) runner.Cancel();
        }
    }

    public void StartRecording(RecordingOptions options)
    {
        lock (_drainGate)
        {
            while (_queue.TryDequeue(out _)) { } // nothing from before the start belongs to the recording
            lock (_gate)
            {
                _recorder = new RecordingBuilder(options);
                _recordStart = Stopwatch.GetTimestamp();
                ApplyHooks();
            }
        }
        Log.Info("Recording started.");
    }

    /// <summary>
    /// Ends the recording, keeping the motion since the last event (a recording may be motion alone).
    /// <paramref name="fromUi"/> trims the trailing samples over Clicket's windows: the pointer on the stop button.
    /// Called while the recording bar is still open, so it is found under those samples.
    /// </summary>
    public List<Step> StopRecording(bool fromUi)
    {
        List<Step> steps;
        lock (_drainGate)
        {
            DrainLocked();
            lock (_gate)
            {
                steps = _recorder?.Finish(fromUi ? IsOwnWindowAt : (Func<int, int, bool>?)null) ?? new List<Step>();
                _recorder = null;
                ApplyHooks();
            }
        }
        Log.Info("Recording stopped with " + steps.Count + " steps.");
        return steps;
    }

    public void Dispose()
    {
        _disposed = true;
        List<MacroRunner> running;
        lock (_gate)
        {
            running = new List<MacroRunner>(_runners.Values);
            foreach (var runner in running) runner.Cancel();
        }
        // Let every runner release what it holds before the process ends (background threads die with it).
        foreach (var runner in running) runner.Join(1000);
        _signal.Set();
        _hook.Dispose();
        _dispatcher.Join(1000);
    }

    /// <summary>Called under _gate. During a recording only the stop and record hotkeys are live, so any other key can be recorded.</summary>
    private void ApplyHooks()
    {
        var recording = _recorder != null;
        var triggers = new HashSet<Hotkey>();
        if (!_suspended)
        {
            if (!_stopHotkey.IsEmpty) triggers.Add(_stopHotkey);
            if (!_recordHotkey.IsEmpty) triggers.Add(_recordHotkey);
            if (!recording) foreach (var hotkey in _byHotkey.Keys) triggers.Add(hotkey);
        }
        var mouseTriggers = false;
        foreach (var hotkey in triggers) mouseTriggers |= hotkey.IsMouse;
        _hook.SetTriggers(triggers);
        _hook.Configure(keyboard: true, mouse: recording || mouseTriggers, forwardKeys: recording, forwardMouse: recording);
    }

    private void Dispatch()
    {
        NativeMethods.UsePhysicalPixels();
        while (!_disposed)
        {
            _signal.WaitOne();
            try
            {
                lock (_drainGate) DrainLocked();
            }
            catch (Exception e)
            {
                Log.Error("Dispatch failed.", e);
            }
        }
    }

    private void DrainLocked()
    {
        while (_queue.TryDequeue(out var e))
        {
            switch (e.Kind)
            {
                case HookEventKind.TriggerDown: OnTriggerDown(e.Hotkey, e.TriggerKey); break;
                case HookEventKind.TriggerUp: OnTriggerUp(e.TriggerKey); break;
                default: Record(e.Raw); break;
            }
        }
    }

    private void OnTriggerDown(Hotkey hotkey, int key)
    {
        if ((hotkey.Modifiers & (Modifiers.Alt | Modifiers.Win)) != 0) InputSender.MaskModifierRelease();
        Macro? macro;
        lock (_gate)
        {
            if (hotkey == _stopHotkey)
            {
                foreach (var runner in _runners.Values) runner.Cancel();
                return;
            }
            if (hotkey == _recordHotkey)
            {
                macro = null;
            }
            else if (!_byHotkey.TryGetValue(hotkey, out macro))
            {
                return;
            }
        }
        if (macro == null)
        {
            RecordHotkeyPressed?.Invoke();
            return;
        }

        switch (macro.Mode)
        {
            case PlayMode.Toggle:
                if (IsRunning(macro.Id)) Stop(macro.Id);
                else Start(macro, 0, 0, hotkey.Modifiers);
                break;
            case PlayMode.WhileHeld:
                lock (_gate) _heldTriggers[key] = macro.Id;
                Start(macro, 0, 0, hotkey.Modifiers);
                break;
            case PlayMode.Repeat:
                Start(macro, macro.RepeatCount, 0, hotkey.Modifiers);
                break;
            default:
                Start(macro, 1, 0, hotkey.Modifiers);
                break;
        }
    }

    private void OnTriggerUp(int key)
    {
        string? id;
        lock (_gate)
        {
            if (!_heldTriggers.TryGetValue(key, out id)) return;
            _heldTriggers.Remove(key);
        }
        Stop(id);
    }

    private void Start(Macro macro, int passes, int startDelayMs, Modifiers releaseFirst)
    {
        MacroRunner runner;
        lock (_gate)
        {
            if (_disposed || _runners.ContainsKey(macro.Id)) return; // one run per macro, like one thread per hotkey
            runner = new MacroRunner(macro, passes, startDelayMs, releaseFirst);
            runner.Finished += OnRunnerFinished;
            _runners[macro.Id] = runner;
        }
        RunStateChanged?.Invoke(macro.Id, true);
        runner.Start();
    }

    private void OnRunnerFinished(MacroRunner runner)
    {
        lock (_gate)
        {
            if (_runners.TryGetValue(runner.MacroId, out var current) && ReferenceEquals(current, runner)) _runners.Remove(runner.MacroId);
        }
        RunStateChanged?.Invoke(runner.MacroId, false);
    }

    private void Record(RawEvent raw)
    {
        RecordingBuilder? recorder;
        lock (_gate) recorder = _recorder;
        if (recorder == null) return;

        // Clicks and typing aimed at Clicket itself (the stop button, the recording bar) are not part of the macro.
        switch (raw.Kind)
        {
            // Releases always pass: the builder drops those without a recorded press, and keeps a press from staying held.
            case RawKind.ButtonDown:
            case RawKind.Wheel:
            case RawKind.HWheel:
                if (IsOwnWindowAt(raw.X, raw.Y)) return;
                break;
            case RawKind.KeyDown:
                if (IsOwnWindow(NativeMethods.GetForegroundWindow())) return;
                break;
        }
        raw.Micros = (long)((raw.Micros - _recordStart) * (1_000_000.0 / Stopwatch.Frequency));
        recorder.Add(raw);
    }

    private bool IsOwnWindowAt(int x, int y) => IsOwnWindow(NativeMethods.WindowFromPoint(new NativeMethods.Point { X = x, Y = y }));

    private bool IsOwnWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        return pid == _ownProcess;
    }
}
