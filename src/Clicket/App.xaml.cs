using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Clicket.Core;
using Clicket.Input;
using Clicket.Native;
using Clicket.Shell;
using Clicket.Ui;

namespace Clicket;

/// <summary>Where a recording goes when it ends: a new macro, or into an existing one at an index.</summary>
internal sealed class RecordTarget
{
    public RecordTarget(Macro? macro, int insertAt)
    {
        Macro = macro;
        InsertAt = insertAt;
    }

    public Macro? Macro { get; }
    public int InsertAt { get; }
}

public partial class App : Application
{
    private const string HostTitle = "Clicket.Host";
    private const int ShowWindowMessage = NativeMethods.WmApp + 2;
    private const long MaxLogBytes = 1024 * 1024;

    private Mutex? _instance;
    private HwndSource? _host;
    private int _taskbarCreated;
    private TrayIcon? _tray;
    private TrayMenuWindow? _menu;
    private MainWindow? _window;
    private PadWindow? _pad;
    private RecordingBar? _recordingBar;
    private RecordTarget? _recordTarget;
    private DispatcherTimer? _saveTimer;
    private RegisteredWaitHandle? _activateWait, _exitWait;
    private EventWaitHandle? _activateEvent, _exitEvent;
    private System.Diagnostics.Process? _parent;
    private bool _persist;
    private bool _exiting;
    private readonly object _saveGate = new object();
    private long _saveSequence, _savedSequence;

    internal AppSettings Settings { get; private set; } = new AppSettings();
    internal List<Macro> Library { get; set; } = new List<Macro>();
    internal Engine? Engine { get; private set; }
    internal HostedOptions? Hosted { get; private set; }
    internal bool IsHosted => Hosted != null;

    /// <summary>False for diagnostics (snapshot, icon export), which must not read or write user state.</summary>
    internal bool IsResident => _persist;
    internal string DataFolder { get; private set; } = "";
    internal static string Version => Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
    private string SettingsPath => Path.Combine(DataFolder, "settings.ini");
    private string LibraryPath => Path.Combine(DataFolder, "macros.json");

    /// <summary>Raised on the UI thread when a macro starts or stops playing.</summary>
    internal event Action<string, bool>? RunStateChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (s, args) => WriteCrashLog((Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (s, args) => { WriteCrashLog(args.Exception); args.SetObserved(); };

        // Diagnostics run before the mutex and the settings, so they cannot persist anything.
        if (e.Args.Length >= 2 && (e.Args[0] == "--export-icon" || e.Args[0] == "--snapshot"))
        {
            Strings.SetLanguage(e.Args.Length >= 3 ? e.Args[2] : Strings.English);
            if (e.Args[0] == "--export-icon") Artwork.WriteIconFile(e.Args[1]);
            else Snapshot.Write(e.Args[1]);
            Shutdown();
            return;
        }

        Hosted = HostedOptions.TryParse(e.Args);
        _instance = new Mutex(true, @"Local\Clicket.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            _instance.Dispose();
            _instance = null;
            if (Hosted != null)
            {
                // A standalone Clicket already owns the hooks; the module waits for it to exit.
                Shutdown(HostedOptions.AlreadyRunningExitCode);
                return;
            }
            // Launching again is the way back to the window.
            var running = NativeMethods.FindWindow(null, HostTitle);
            NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);
            if (running != IntPtr.Zero) NativeMethods.PostMessage(running, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        _persist = true;
        DataFolder = Hosted?.DataDirectory ?? Path.Combine(UserFolders.Roaming, "Clicket");
        Directory.CreateDirectory(DataFolder);
        Log.Initialize(Path.Combine(DataFolder, "clicket.log"));
        Log.Info("Clicket " + Version + (IsHosted ? " (hosted)" : "") + " starting.");
        StartProfile();
        Settings = AppSettings.Load(SettingsPath);
        Library = MacroStore.Load(LibraryPath, Log.Warn);
        ApplyLanguage();

        Engine = new Engine();
        Engine.RunStateChanged += (id, running) => Dispatcher.BeginInvoke(new Action(() => OnRunStateChanged(id, running)));
        Engine.RecordHotkeyPressed += () => Dispatcher.BeginInvoke(new Action(ToggleRecordingFromHotkey));
        KeyCaptureBox.ListeningChanged += listening => Engine?.SuspendTriggers(listening);
        ApplyEngineConfiguration();

        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _host = new HwndSource(new HwndSourceParameters(HostTitle) { WindowStyle = NativeMethods.WsPopup, ExtendedWindowStyle = NativeMethods.WsExToolWindow, Width = 0, Height = 0 });
        _host.AddHook(HostProc);

        // Both shapes answer the module's "open" request: a standalone copy started first (Run key) is the one the user sees.
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, HostedOptions.ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (s, timedOut) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)), null, Timeout.Infinite, false);

        if (Hosted != null)
        {
            WatchHost(Hosted);
            if (Hosted.Show) ShowMainWindow();
        }
        else
        {
            _tray = new TrayIcon(_host.Handle);
            _tray.Show(Artwork.CreateAppIconHandle(), "Clicket");
            if (!e.Args.Contains(StartupRegistration.StartupArgument)) ShowMainWindow();
            else ReleaseMemory();
        }
        if (Settings.PadVisible) Dispatcher.BeginInvoke(new Action(() => SetPadVisible(true)), DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        try
        {
            if (Engine?.IsRecording == true) Engine.StopRecording(fromUi: true);
            Engine?.Dispose();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
        }
        _tray?.Dispose();
        _host?.Dispose();
        _activateWait?.Unregister(null);
        _exitWait?.Unregister(null);
        _window?.CapturePlacement(Settings);
        FlushSave();
        SaveSettings();
        Log.Info("Clicket exiting.");
        _instance?.ReleaseMutex(); // after the final save: a new instance must find the final state
        _instance?.Dispose();
        base.OnExit(e);
    }

    // ---------- Windows ----------

    internal void ShowMainWindow()
    {
        _menu?.Close();
        if (_window == null)
        {
            _window = new MainWindow();
            _window.Closed += (s, e) =>
            {
                _window = null;
                ReleaseMemory();
            };
            _window.RestorePlacement(Settings);
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        if (!_window.IsVisible) _window.Show();
        _window.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(_window).Handle);
    }

    internal void ShowSettings()
    {
        ShowMainWindow();
        _window?.OpenSettings();
    }

    /// <summary>The window's close button: hide while hotkeys must keep working, exit when the user prefers that.</summary>
    internal bool ShouldHideOnClose => _persist && !_exiting && (IsHosted || Settings.HideOnClose);

    internal void Quit()
    {
        if (!_persist) return; // snapshots close their windows without ending the run
        _exiting = true;
        Shutdown();
    }

    internal bool PadVisible => _pad != null;

    internal void SetPadVisible(bool visible)
    {
        if (visible && _pad == null)
        {
            _pad = new PadWindow();
            _pad.Closed += (s, e) =>
            {
                _pad = null;
                if (!_exiting)
                {
                    Settings.PadVisible = false;
                    SaveSettings();
                    _window?.SyncPadToggle();
                }
            };
            _pad.ShowNear(Settings);
        }
        else if (!visible && _pad != null)
        {
            _pad.Close();
        }
        Settings.PadVisible = visible;
        SaveSettings();
        _window?.SyncPadToggle();
    }

    // ---------- Library ----------

    /// <summary>Something in the library changed: refresh hotkeys now, save shortly after (the newest snapshot wins).</summary>
    internal void LibraryChanged()
    {
        ApplyEngineConfiguration();
        _pad?.Refresh();
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer!.Stop();
                SaveLibraryAsync();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    internal void ApplyEngineConfiguration() => Engine?.Configure(Library, Settings.StopHotkey, Settings.RecordHotkey);

    private void SaveLibraryAsync()
    {
        if (!_persist) return;
        var snapshot = Library.Select(m => m.Clone()).ToList();
        var sequence = ++_saveSequence;
        Task.Run(() => SaveLibrary(snapshot, sequence));
    }

    private void FlushSave()
    {
        if (_saveTimer?.IsEnabled == true) _saveTimer.Stop();
        if (_persist) SaveLibrary(Library.Select(m => m.Clone()).ToList(), ++_saveSequence);
    }

    /// <summary>Snapshots are numbered on the UI thread; a write older than the last one written is skipped, so the newest always wins.</summary>
    private void SaveLibrary(List<Macro> snapshot, long sequence)
    {
        lock (_saveGate)
        {
            if (sequence <= _savedSequence) return;
            _savedSequence = sequence;
            try
            {
                MacroStore.Save(LibraryPath, snapshot);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Error("Library save failed.", e);
            }
        }
    }

    internal void SaveSettings()
    {
        if (!_persist) return;
        try
        {
            Settings.Save(SettingsPath);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Log.Error("Settings save failed.", e);
        }
    }

    /// <summary>"auto" follows SwissTechKnife's language, re-read whenever the window comes forward.</summary>
    internal void ApplyLanguage() => Strings.SetLanguage(LanguageResolver.Resolve(Settings.Language, LanguageResolver.StkSettingsPath()));

    // ---------- Playback ----------

    private void OnRunStateChanged(string id, bool running)
    {
        RunStateChanged?.Invoke(id, running);
        _pad?.SetRunning(id, running);
    }

    // ---------- Recording ----------

    internal bool IsRecording => _recordTarget != null;

    internal void BeginRecording(RecordTarget target)
    {
        if (Engine == null || _recordTarget != null) return;
        _recordTarget = target;
        Engine.StartRecording(Settings.ToRecordingOptions());
        _recordingBar = new RecordingBar(Settings.RecordHotkey);
        _recordingBar.StopRequested += () => EndRecording(fromUi: true);
        _recordingBar.ShowOnScreen(_window);
        _window?.OnRecordingStateChanged(true);
    }

    internal void EndRecording(bool fromUi)
    {
        if (Engine == null || _recordTarget == null) return;
        var steps = Engine.StopRecording(fromUi);
        var target = _recordTarget;
        _recordTarget = null;
        _recordingBar?.Close();
        _recordingBar = null;
        ShowMainWindow();
        _window?.OnRecordingStateChanged(false);
        _window?.OnRecordingFinished(target, steps);
    }

    private void ToggleRecordingFromHotkey()
    {
        if (_recordTarget != null) EndRecording(fromUi: false);
        else BeginRecording(new RecordTarget(null, 0));
    }

    internal int RecordedCount => Engine?.RecordedCount ?? 0;

    // ---------- Host window, tray, hosted mode ----------

    private IntPtr HostProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == _taskbarCreated) _tray?.Restore();
        else if (message == NativeMethods.WmClose) Quit(); // the installer asks the running app to leave before replacing it
        else if (message == ShowWindowMessage) ShowMainWindow();
        else if (message == TrayIcon.CallbackMessage)
        {
            var trayEvent = lParam.ToInt64() & 0xFFFF;
            if (trayEvent == NativeMethods.NinSelect || trayEvent == NativeMethods.NinKeySelect) ShowMainWindow();
            else if (trayEvent == NativeMethods.WmContextMenu) ShowTrayMenu();
        }
        else return IntPtr.Zero;
        handled = true;
        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        _menu?.Close();
        NativeMethods.GetCursorPos(out var cursor);
        _menu = new TrayMenuWindow();
        _menu.Closed += (s, e) =>
        {
            _menu = null;
            ReleaseMemory();
        };
        _menu.ShowAt(cursor);
    }

    /// <summary>The module asks this copy to show its window or to exit; the copy also leaves with its parent.</summary>
    private void WatchHost(HostedOptions hosted)
    {
        _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, HostedOptions.ExitEventName);
        _exitEvent.Reset();
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (s, timedOut) => Dispatcher.BeginInvoke(new Action(Quit)), null, Timeout.Infinite, true);
        if (hosted.ParentId <= 0) return;
        try
        {
            _parent = System.Diagnostics.Process.GetProcessById(hosted.ParentId);
            _parent.EnableRaisingEvents = true;
            _parent.Exited += (s, e) => Dispatcher.BeginInvoke(new Action(Quit));
            if (_parent.HasExited) Dispatcher.BeginInvoke(new Action(Quit));
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
        {
            Dispatcher.BeginInvoke(new Action(Quit));
        }
    }

    /// <summary>A resident app should not sit on what a closed screen used.</summary>
    internal void ReleaseMemory() => Dispatcher.BeginInvoke(new Action(() =>
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }), DispatcherPriority.ApplicationIdle);

    private void StartProfile()
    {
        try
        {
            var folder = Path.Combine(UserFolders.Local, "Clicket");
            Directory.CreateDirectory(folder);
            ProfileOptimization.SetProfileRoot(folder);
            ProfileOptimization.StartProfile(IsHosted ? "hosted.profile" : "startup.profile");
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // Startup is only slower without the profile.
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.Handled = _persist; // a resident app outlives one failed action
        if (_persist) return;
        // A diagnostic fails fast: no dialog, nothing written, and a non-zero exit code.
        Console.Error.WriteLine(e.Exception);
        Environment.Exit(1);
    }

    private void WriteCrashLog(Exception exception)
    {
        if (!_persist) return;
        Log.Error("Unhandled exception.", exception);
        try
        {
            var log = new FileInfo(Path.Combine(DataFolder, "crash.log"));
            if (log.Exists && log.Length > MaxLogBytes) log.Delete();
            File.AppendAllText(log.FullName, DateTime.Now.ToString("s") + " " + exception + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // Disk trouble must never take a resident app down.
        }
    }
}
