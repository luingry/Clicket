using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SwissTechKnife.Sdk;

namespace Clicket.Module;

public enum HostedAppState
{
    Stopped,
    Starting,
    Running,
    /// <summary>A standalone Clicket owns the hooks; the module waits for it to exit.</summary>
    RunningOutside,
    /// <summary>The app crashed repeatedly; restarts stopped until the user opens it again.</summary>
    Failed,
}

/// <summary>
/// Runs Clicket in its own process (<c>--hosted</c>), so its hooks and playback threads never share the host's
/// UI thread, GC or crashes. Restarts it after a crash (at most 3 times a minute) and stops it with the module.
/// Thread-safe; <see cref="StateChanged"/> is raised on arbitrary threads.
/// </summary>
internal sealed class HostedAppSupervisor : IDisposable
{
    // Must match Clicket.Core.HostedOptions and the app's single-instance mutex.
    private const string ExitEventName = @"Local\Clicket.Hosted.Exit";
    private const string ActivateEventName = @"Local\Clicket.App.Activate";
    private const int AlreadyRunningExitCode = 3;
    private const string StandaloneProcessName = "Clicket";

    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(1);
    private const int MaxCrashesPerWindow = 3;
    private static readonly TimeSpan OutsidePollInterval = TimeSpan.FromSeconds(5);
    private const int ExitTimeoutMs = 3000;

    private readonly object _gate = new object();
    private readonly string _exePath;
    private readonly string _dataDirectory;
    private readonly IModuleLog _log;
    private readonly Queue<DateTime> _crashes = new Queue<DateTime>();
    private Process? _process;
    private Timer? _retry;
    private bool _stopping;
    private bool _disposed;
    private HostedAppState _state = HostedAppState.Stopped;

    public HostedAppSupervisor(string exePath, string dataDirectory, IModuleLog log)
    {
        _exePath = exePath;
        _dataDirectory = dataDirectory;
        _log = log;
    }

    public event Action<HostedAppState>? StateChanged;

    public HostedAppState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Launches the app hidden on a pool thread; returns at once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopping = false;
        }
        ThreadPool.QueueUserWorkItem(_ => Launch(show: false));
    }

    /// <summary>Shows the app's window, starting the app first when it is not running.</summary>
    public void ShowWindow()
    {
        HostedAppState state;
        lock (_gate)
        {
            if (_disposed) return;
            _stopping = false;
            state = _state;
        }

        if (state == HostedAppState.Running || state == HostedAppState.RunningOutside || state == HostedAppState.Starting)
        {
            // The host is in the foreground (the user clicked); let the app take focus.
            AllowSetForegroundWindow(AsfwAny);
            if (SignalNamedEvent(ActivateEventName)) return;
        }

        lock (_gate) _crashes.Clear();
        ThreadPool.QueueUserWorkItem(_ => Launch(show: true));
    }

    /// <summary>Asks the app to save and exit; kills it after a short timeout. Blocks at most about 4 s.</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            _stopping = true;
            _retry?.Dispose();
            _retry = null;
            process = _process;
        }

        if (process != null)
        {
            try
            {
                using var exit = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName);
                exit.Set();
                if (!process.WaitForExit(ExitTimeoutMs))
                {
                    _log.Warn("Clicket did not exit in time; terminating it.");
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch (Exception error) when (error is InvalidOperationException || error is System.ComponentModel.Win32Exception)
            {
                // Already exited.
            }
        }

        SetState(HostedAppState.Stopped);
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) _disposed = true;
    }

    private void Launch(bool show)
    {
        lock (_gate)
        {
            if (_disposed || _stopping || _process != null) return;
            _retry?.Dispose();
            _retry = null;
        }

        // A standalone Clicket already runs every hotkey; a second copy would double them.
        if (IsStandaloneRunning())
        {
            if (show)
            {
                AllowSetForegroundWindow(AsfwAny);
                SignalNamedEvent(ActivateEventName);
            }
            EnterRunningOutside();
            return;
        }

        if (!File.Exists(_exePath))
        {
            _log.Error("Clicket executable not found: " + _exePath);
            SetState(HostedAppState.Failed);
            return;
        }

        SetState(HostedAppState.Starting);
        try
        {
            var args = "--hosted --parent " + Process.GetCurrentProcess().Id + " --data \"" + _dataDirectory.TrimEnd('\\') + "\"";
            if (show)
            {
                args += " --show";
                AllowSetForegroundWindow(AsfwAny);
            }
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(_exePath, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(_exePath)!,
                },
                EnableRaisingEvents = true,
            };
            process.Exited += OnExited;
            lock (_gate)
            {
                if (_stopping || _disposed)
                {
                    process.Dispose();
                    SetState(HostedAppState.Stopped);
                    return;
                }
                process.Start();
                _process = process;
            }
            _log.Info("Clicket started (pid " + process.Id + ").");
            SetState(HostedAppState.Running);
        }
        catch (Exception error)
        {
            _log.Error("Could not start Clicket.", error);
            SetState(HostedAppState.Failed);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var process = (Process)sender!;
        int code;
        try { code = process.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        process.Dispose();

        bool stopping;
        lock (_gate)
        {
            if (ReferenceEquals(_process, process)) _process = null;
            stopping = _stopping || _disposed;
        }
        if (stopping) return;

        if (code == AlreadyRunningExitCode)
        {
            EnterRunningOutside();
            return;
        }
        if (code == 0)
        {
            // A clean exit the host did not ask for (the user chose Exit somewhere): stay stopped.
            _log.Info("Clicket exited.");
            SetState(HostedAppState.Stopped);
            return;
        }

        _log.Warn("Clicket exited unexpectedly (code " + code + ").");
        int recent;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _crashes.Enqueue(now);
            while (_crashes.Count > 0 && now - _crashes.Peek() > CrashWindow) _crashes.Dequeue();
            recent = _crashes.Count;
        }

        if (recent >= MaxCrashesPerWindow)
        {
            _log.Error("Clicket keeps exiting; automatic restarts stopped.");
            SetState(HostedAppState.Failed);
            return;
        }

        SetState(HostedAppState.Starting);
        ScheduleLaunch(TimeSpan.FromSeconds(recent));
    }

    /// <summary>Polls the process list (cheap, never the mutex) until the standalone app exits, then takes over.</summary>
    private void EnterRunningOutside()
    {
        SetState(HostedAppState.RunningOutside);
        lock (_gate)
        {
            if (_stopping || _disposed) return;
            _retry?.Dispose();
            _retry = new Timer(_ =>
            {
                if (!IsStandaloneRunning()) Launch(show: false);
            }, null, OutsidePollInterval, OutsidePollInterval);
        }
    }

    private void ScheduleLaunch(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_stopping || _disposed) return;
            _retry?.Dispose();
            _retry = new Timer(_ => Launch(show: false), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SetState(HostedAppState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception error)
        {
            _log.Error("State handler failed.", error);
        }
    }

    /// <summary>The installed standalone is Clicket.exe; the hosted copy has its own image name and is excluded.</summary>
    private static bool IsStandaloneRunning()
    {
        var found = false;
        foreach (var p in Process.GetProcessesByName(StandaloneProcessName))
        {
            found = true;
            p.Dispose();
        }
        return found;
    }

    private static bool SignalNamedEvent(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException || error is UnauthorizedAccessException || error is IOException)
        {
            return false;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
