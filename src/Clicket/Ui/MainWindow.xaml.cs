using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Clicket.Core;
using MouseAction = Clicket.Core.MouseAction;
using MouseButton = Clicket.Core.MouseButton;
using Clicket.Native;

namespace Clicket.Ui;

/// <summary>
/// The editor shell: library on the left, the selected macro's trigger and timeline in the middle,
/// Insert and the step inspector on the right. Edits apply to the live library and save on a short debounce.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxUndo = 60;
    private const int CountdownSeconds = 3;

    private readonly ObservableCollection<MacroItem> _items = new ObservableCollection<MacroItem>();
    private readonly ObservableCollection<StepItem> _steps = new ObservableCollection<StepItem>();
    private readonly Dictionary<string, Stack<List<Step>>> _undo = new Dictionary<string, Stack<List<Step>>>();
    private static List<Step> _clipboard = new List<Step>();
    private MacroItem? _current;
    private bool _loading;
    private string _lastUndoGroup = "";
    private DateTime _lastUndoAt;
    private DispatcherTimer? _countdown;
    private int _countdownLeft;
    private DispatcherTimer? _toastTimer;
    private bool _deleteArmed;

    public MainWindow()
    {
        InitializeComponent();
        Logo.Source = Artwork.AppIconImage();
        NoMacroLogo.Source = Artwork.AppIconImage();
        Icon = Artwork.Render(32, 32, dc => Artwork.DrawAppIcon(dc, 32));
        LibraryList.ItemsSource = _items;
        StepsList.ItemsSource = _steps;

        foreach (var macro in Host.Library) _items.Add(new MacroItem(macro) { IsRunning = Host.Engine?.IsRunning(macro.Id) == true });
        HotkeyBox.Captured += OnHotkeyCaptured;
        ModeSelector.SelectionChanged += OnModeChanged;
        StepEditor.Changing += group => PushUndo(group);
        StepEditor.Changed += OnStepEdited;
        Host.RunStateChanged += OnRunStateChanged;
        Strings.LanguageChanged += OnLanguageChanged;
        MoreMenu.Closed += (s, e) => DisarmDelete();

        SourceInitialized += (s, e) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.StyleFrame(handle);
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        };
        StateChanged += (s, e) => SyncMaximize();
        Activated += (s, e) =>
        {
            if (Host.IsResident && Host.Settings.Language == AppSettings.LanguageAuto) Host.ApplyLanguage();
        };
        Closing += OnClosing;
        Closed += (s, e) =>
        {
            Host.RunStateChanged -= OnRunStateChanged;
            Strings.LanguageChanged -= OnLanguageChanged;
        };
        PreviewKeyDown += OnWindowKeyDown;

        UpdateLibraryEmpty();
        SyncPadToggle();
        if (_items.Count > 0) LibraryList.SelectedIndex = 0;
        else ShowMacro(null);
    }

    private static App Host => (App)Application.Current;

    // ======================================================== Placement and chrome

    internal void RestorePlacement(AppSettings s)
    {
        if (s.WindowWidth < (int)MinWidth || s.WindowHeight < (int)MinHeight) return;
        var area = SystemParameters.VirtualScreenLeft;
        var rect = new Rect(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight);
        var desktop = new Rect(area, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!desktop.IntersectsWith(rect)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = s.WindowX;
        Top = s.WindowY;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    internal void CapturePlacement(AppSettings s)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0) return;
        s.WindowX = (int)bounds.Left;
        s.WindowY = (int)bounds.Top;
        s.WindowWidth = (int)bounds.Width;
        s.WindowHeight = (int)bounds.Height;
        s.WindowMaximized = WindowState == WindowState.Maximized;
    }

    private void SyncMaximize()
    {
        var maximized = WindowState == WindowState.Maximized;
        // A maximized chrome window overhangs the screen by the frame width; pad it back in.
        Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = Artwork.Token<string>(maximized ? "Glyph.Restore" : "Glyph.Maximize");
        MaximizeButton.ToolTip = Strings.Get(maximized ? "Restore" : "Maximize");
    }

    /// <summary>Snap Layouts: Windows shows them when the maximize button answers HTMAXBUTTON.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmNcHitTest || !MaximizeButton.IsVisible) return IntPtr.Zero;
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        try
        {
            var point = MaximizeButton.PointFromScreen(new Point(x, y));
            if (point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight)
            {
                handled = true;
                return new IntPtr(9); // HTMAXBUTTON
            }
        }
        catch (InvalidOperationException)
        {
            // Not connected to a presentation source yet.
        }
        return IntPtr.Zero;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CapturePlacement(Host.Settings);
        Host.SaveSettings();
        if (Host.ShouldHideOnClose)
        {
            e.Cancel = true;
            Settings.Hide();
            Hide();
            Host.ReleaseMemory();
            return;
        }
        Host.Quit();
    }

    internal void OpenSettings(bool animate = true) => Settings.ShowPanel(animate);

    /// <summary>--snapshot: a macro and a step selected, as a user would leave them.</summary>
    internal void SelectForSnapshot(int macro, int step)
    {
        if (macro >= _items.Count) return;
        LibraryList.SelectedIndex = macro;
        if (step < _steps.Count) RebuildSteps(new[] { step });
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings();

    internal void SyncPadToggle() => PadToggle.IsChecked = Host.PadVisible;

    private void OnPadToggle(object sender, RoutedEventArgs e) => Host.SetPadVisible(PadToggle.IsChecked == true);

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        foreach (var item in _items) item.Refresh();
        foreach (var step in _steps) step.Refresh();
        UpdateSummary();
        UpdateLibraryEmpty();
        SyncMaximize();
        ShowHotkeyMessage();
    }));

    // ======================================================== Library

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var filter = SearchBox.Text.Trim();
        foreach (var item in _items) item.IsVisible = item.Matches(filter);
        UpdateLibraryEmpty();
    }

    private void UpdateLibraryEmpty()
    {
        var filter = SearchBox.Text.Trim();
        var any = _items.Any(i => i.IsVisible);
        LibraryEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (_items.Count == 0)
        {
            LibraryEmptyTitle.Text = Strings.Get("EmptyLibraryTitle");
            LibraryEmptyBody.Text = Strings.Get("EmptyLibraryBody");
            LibraryEmptyBody.Visibility = Visibility.Visible;
        }
        else
        {
            LibraryEmptyTitle.Text = Strings.Format("NoMatches", filter);
            LibraryEmptyBody.Visibility = Visibility.Collapsed;
        }
    }

    private void OnLibrarySelection(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is MacroItem item) ShowMacro(item);
        else if (_current != null && !_items.Contains(_current)) ShowMacro(null);
    }

    private MacroItem AddMacro(Macro macro, int index = -1)
    {
        if (index < 0 || index > Host.Library.Count) index = Host.Library.Count;
        Host.Library.Insert(index, macro);
        var item = new MacroItem(macro);
        _items.Insert(index, item);
        Host.LibraryChanged();
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        UpdateLibraryEmpty();
        LibraryList.SelectedItem = item;
        LibraryList.ScrollIntoView(item);
        return item;
    }

    private void OnNewMacro(object sender, RoutedEventArgs e)
    {
        AddMacro(new Macro { Name = UniqueName(Strings.Get("NewMacroName")) });
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void OnRecordNew(object sender, RoutedEventArgs e) => Host.BeginRecording(new RecordTarget(null, 0));

    private void OnRecordInto(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        Host.BeginRecording(new RecordTarget(_current.Macro, InsertionIndex()));
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Clicket (*.clicket)|*.clicket|JSON (*.json)|*.json", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        MacroItem? last = null;
        foreach (var file in dialog.FileNames)
        {
            Macro? macro = null;
            try
            {
                macro = MacroStore.ImportMacro(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn("Import failed: " + ex.GetType().Name);
            }
            if (macro == null)
            {
                ShowToast(Strings.Get("ImportFailed"));
                continue;
            }
            macro.Hotkey = ResolveConflict(macro.Hotkey, macro.Id);
            last = AddMacro(macro);
            ShowToast(Strings.Format("Imported", last.Name));
        }
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        DisarmDelete();
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.IsOpen = true;
    }

    private void OnDuplicateMacro(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var copy = _current.Macro.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = UniqueName(Strings.Format("CopySuffix", _current.Name));
        copy.Hotkey = default; // two macros never share a hotkey
        copy.Created = DateTime.UtcNow;
        AddMacro(copy, _items.IndexOf(_current) + 1);
    }

    private void OnExportMacro(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var name = string.Concat(_current.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Clicket (*.clicket)|*.clicket", FileName = (name.Length == 0 ? "macro" : name) + ".clicket" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            AtomicFile.WriteAllText(dialog.FileName, MacroStore.ExportMacro(_current.Macro));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Log.Warn("Export failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Destructive: the first click arms, the second deletes.</summary>
    private void OnDeleteMacro(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (!_deleteArmed)
        {
            _deleteArmed = true;
            DeleteMenuItem.Header = Strings.Get("ConfirmDelete");
            return;
        }
        MoreMenu.IsOpen = false;
        var item = _current;
        Host.Engine?.Stop(item.Macro.Id);
        var index = _items.IndexOf(item);
        Host.Library.Remove(item.Macro);
        _items.Remove(item);
        _undo.Remove(item.Macro.Id);
        Host.LibraryChanged();
        UpdateLibraryEmpty();
        if (_items.Count > 0) LibraryList.SelectedIndex = Math.Min(index, _items.Count - 1);
        else ShowMacro(null);
    }

    private void DisarmDelete()
    {
        _deleteArmed = false;
        DeleteMenuItem.Header = Strings.Get("Delete");
    }

    private string UniqueName(string baseName)
    {
        var names = new HashSet<string>(Host.Library.Select(m => m.Name), StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = baseName + " " + i.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(candidate)) return candidate;
        }
    }

    // ======================================================== Macro header

    private void ShowMacro(MacroItem? item)
    {
        _loading = true;
        try
        {
            StopCountdown();
            _current = item;
            Editor.Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
            NoMacro.Visibility = item == null ? Visibility.Visible : Visibility.Collapsed;
            Tiles.IsEnabled = item != null;
            if (item == null)
            {
                _steps.Clear();
                StepEditor.Load(null);
                return;
            }
            var macro = item.Macro;
            NameBox.Text = macro.Name;
            HotkeyBox.Value = macro.Hotkey;
            ModeSelector.SelectedIndex = (int)macro.Mode;
            RepeatBox.Text = macro.RepeatCount.ToString(CultureInfo.InvariantCulture);
            RepeatPanel.Visibility = macro.Mode == PlayMode.Repeat ? Visibility.Visible : Visibility.Collapsed;
            EnabledSwitch.IsChecked = macro.Enabled;
            PadSwitch.IsChecked = macro.OnPad;
            ShowHotkeyMessage();
            SyncRunButtons();
            RebuildSteps(macro.Steps.Count > 0 ? new[] { 0 } : new int[0]);
        }
        finally
        {
            _loading = false;
        }
    }

    private void MacroEdited(bool hotkeys = false)
    {
        _current?.Refresh();
        Host.LibraryChanged();
        if (hotkeys) ShowHotkeyMessage();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current == null) return;
        _current.Macro.Name = NameBox.Text.Trim();
        MacroEdited();
    }

    private void OnHotkeyCaptured(Hotkey hotkey)
    {
        if (_current == null) return;
        _current.Macro.Hotkey = hotkey;
        MacroEdited(hotkeys: true);
    }

    private void OnClearHotkey(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.Macro.Hotkey = default;
        HotkeyBox.Value = default;
        MacroEdited(hotkeys: true);
    }

    /// <summary>One fact, one place: a clash is explained under the field, and the older macro keeps the hotkey.</summary>
    private void ShowHotkeyMessage()
    {
        string? message = null;
        var hotkey = _current?.Macro.Hotkey ?? default;
        if (_current != null && !hotkey.IsEmpty)
        {
            if (hotkey == Host.Settings.StopHotkey) message = Strings.Format("HotkeyReserved", Strings.Get("StopAllHotkey"));
            else if (hotkey == Host.Settings.RecordHotkey) message = Strings.Format("HotkeyReserved", Strings.Get("RecordHotkey"));
            else
            {
                var owner = Host.Library.FirstOrDefault(m => m != _current.Macro && m.Enabled && m.Hotkey == hotkey);
                if (owner != null && Host.Library.IndexOf(owner) < Host.Library.IndexOf(_current.Macro))
                    message = Strings.Format("HotkeyTaken", owner.Name.Length == 0 ? Strings.Get("NewMacroName") : owner.Name);
            }
        }
        HotkeyMessage.Text = message ?? "";
        HotkeyMessage.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private Hotkey ResolveConflict(Hotkey hotkey, string id)
    {
        if (hotkey.IsEmpty) return hotkey;
        if (hotkey == Host.Settings.StopHotkey || hotkey == Host.Settings.RecordHotkey) return default;
        return Host.Library.Any(m => m.Id != id && m.Hotkey == hotkey) ? default : hotkey;
    }

    private void OnModeChanged(int index)
    {
        if (_loading || _current == null) return;
        _current.Macro.Mode = (PlayMode)index;
        RepeatPanel.Visibility = _current.Macro.Mode == PlayMode.Repeat ? Visibility.Visible : Visibility.Collapsed;
        MacroEdited();
        UpdateSummary();
    }

    private void OnRepeatChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current == null) return;
        if (!int.TryParse(RepeatBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) return;
        _current.Macro.RepeatCount = count;
        MacroEdited();
    }

    private void OnEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.Macro.Enabled = EnabledSwitch.IsChecked == true;
        MacroEdited(hotkeys: true);
    }

    private void OnPadChanged(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.Macro.OnPad = PadSwitch.IsChecked == true;
        MacroEdited();
    }

    // ======================================================== Play

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_current == null || Host.Engine == null) return;
        if (_countdown != null)
        {
            StopCountdown();
            return;
        }
        _countdownLeft = CountdownSeconds;
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (s, args) =>
        {
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                PlayButton.Content = Strings.Format("StartingIn", _countdownLeft);
                return;
            }
            var macro = _current?.Macro;
            StopCountdown();
            if (macro == null) return;
            var passes = macro.Mode == PlayMode.Repeat ? macro.RepeatCount : 1;
            Host.Engine?.Play(macro, 0, passes);
        };
        PlayButton.Content = Strings.Format("StartingIn", _countdownLeft);
        _countdown.Start();
    }

    private void StopCountdown()
    {
        _countdown?.Stop();
        _countdown = null;
        PlayButton.SetBinding(ContentProperty, new System.Windows.Data.Binding("[Play]") { Source = Loc.Instance });
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_current != null) Host.Engine?.Stop(_current.Macro.Id);
    }

    private void OnRunStateChanged(string id, bool running)
    {
        var item = _items.FirstOrDefault(i => i.Macro.Id == id);
        if (item != null) item.IsRunning = running;
        if (_current?.Macro.Id == id) SyncRunButtons();
    }

    private void SyncRunButtons()
    {
        var running = _current?.IsRunning == true;
        PlayButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    // ======================================================== Recording

    internal void OnRecordingStateChanged(bool recording)
    {
        IsEnabled = !recording;
    }

    internal void OnRecordingFinished(RecordTarget target, List<Step> steps)
    {
        if (steps.Count == 0)
        {
            ShowToast(Strings.Get("RecordingNothing"));
            return;
        }
        if (target.Macro == null)
        {
            var name = UniqueName(Strings.Format("RecordedMacroName", DateTime.Now.ToString("t", Strings.Culture)));
            AddMacro(new Macro { Name = name, Steps = steps });
            return;
        }
        var item = _items.FirstOrDefault(i => i.Macro == target.Macro);
        if (item == null) return; // deleted meanwhile
        LibraryList.SelectedItem = item;
        PushUndo("record", force: true);
        var at = Math.Max(0, Math.Min(target.InsertAt, target.Macro.Steps.Count));
        target.Macro.Steps.InsertRange(at, steps);
        StepsChanged(Enumerable.Range(at, steps.Count));
    }

    // ======================================================== Timeline

    private void RebuildSteps(IEnumerable<int> select)
    {
        _steps.Clear();
        if (_current == null) return;
        var macro = _current.Macro;
        var program = MacroProgram.Build(macro.Steps, out var error);
        for (var i = 0; i < macro.Steps.Count; i++)
            _steps.Add(new StepItem(macro.Steps[i]) { Index = i, Depth = program.Depth[i], IsError = error == i });
        ScaleBars();
        UpdateSummary(error);
        StepsEmpty.Visibility = _steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        StepsList.SelectedItems.Clear();
        StepItem? last = null;
        foreach (var index in select)
        {
            if (index < 0 || index >= _steps.Count) continue;
            last = _steps[index];
            StepsList.SelectedItems.Add(last);
        }
        if (last != null)
        {
            StepsList.ScrollIntoView(last);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (StepsList.ItemContainerGenerator.ContainerFromItem(last) is ListBoxItem container && !(Keyboard.FocusedElement is TextBox)) container.Focus();
            }), DispatcherPriority.Loaded);
        }
        StepEditor.Load(StepsList.SelectedItems.Count == 1 ? last : null);
        SyncStepButtons();
    }

    /// <summary>Bars share one scale per macro; a square root keeps a 30 s wait from flattening every short step.</summary>
    private void ScaleBars()
    {
        var max = _steps.Count == 0 ? 0 : _steps.Max(s => s.Millis);
        foreach (var step in _steps) step.Bar = max <= 0 ? 0 : Math.Sqrt(step.Millis / (double)max);
    }

    private void UpdateSummary(int error = -2)
    {
        if (_current == null) return;
        var macro = _current.Macro;
        if (error == -2) MacroProgram.Build(macro.Steps, out error);
        var count = macro.Steps.Count == 1 ? Strings.Get("StepCountOne") : Strings.Format("StepCount", macro.Steps.Count);
        var estimate = MacroProgram.EstimateMillis(macro.Steps);
        var endless = estimate == null || macro.Mode == PlayMode.WhileHeld || macro.Mode == PlayMode.Toggle;
        StepsSummary.Text = macro.Steps.Count == 0 ? ""
            : endless ? Strings.Format("StepsSummaryEndless", count)
            : estimate!.Value < 1 ? count // instant macros have no duration worth stating
            : Strings.Format("StepsSummary", count, Strings.Duration(estimate.Value));
        LoopBanner.Visibility = error >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (error >= 0) LoopBannerText.Text = Strings.Format("LoopUnmatched", error + 1);
    }

    private List<int> SelectedIndexes() => StepsList.SelectedItems.Cast<StepItem>().Select(s => s.Index).OrderBy(i => i).ToList();

    /// <summary>New steps go after the selection, or at the end.</summary>
    private int InsertionIndex()
    {
        var selected = SelectedIndexes();
        return selected.Count == 0 ? _current?.Macro.Steps.Count ?? 0 : selected[selected.Count - 1] + 1;
    }

    private void OnStepSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        StepEditor.Load(StepsList.SelectedItems.Count == 1 ? (StepItem)StepsList.SelectedItems[0]! : null);
        SyncStepButtons();
        _lastUndoGroup = ""; // typing in another step starts a new undo entry
    }

    private void SyncStepButtons()
    {
        var selected = SelectedIndexes();
        var any = selected.Count > 0;
        DeleteButton.IsEnabled = any;
        DuplicateButton.IsEnabled = any;
        WrapButton.IsEnabled = any;
        UpButton.IsEnabled = any && selected[0] > 0;
        DownButton.IsEnabled = any && selected[selected.Count - 1] < _steps.Count - 1;
    }

    private void StepsChanged(IEnumerable<int> select)
    {
        _current?.Refresh();
        Host.LibraryChanged();
        RebuildSteps(select);
    }

    private void OnStepEdited(StepItem item)
    {
        _current?.Refresh();
        Host.LibraryChanged();
        if (item.IsLoopMarker || item.Step.Kind == StepKind.Delay || item.Step.Kind == StepKind.Text || item.Step.Kind == StepKind.Path)
        {
            ScaleBars();
            UpdateSummary();
        }
    }

    private void OnInsert(object sender, RoutedEventArgs e)
    {
        if (_current == null || !(sender is Button button)) return;
        var kind = (string)button.Tag;
        var at = InsertionIndex();
        var steps = _current.Macro.Steps;
        PushUndo("insert", force: true);
        switch (kind)
        {
            case "Key":
                steps.Insert(at, Step.KeyStep(0, 0, false, KeyAction.Press));
                break;
            case "Mouse":
                steps.Insert(at, Step.MouseStep(MouseAction.Click));
                break;
            case "Text":
                steps.Insert(at, new Step { Kind = StepKind.Text });
                break;
            case "Delay":
                steps.Insert(at, Step.DelayStep(100));
                break;
            case "Loop":
                // A loop wraps the selection when there is one; otherwise it starts empty.
                var selected = SelectedIndexes();
                if (selected.Count > 1)
                {
                    WrapInLoop(selected[0], selected[selected.Count - 1]);
                    return;
                }
                steps.Insert(at, new Step { Kind = StepKind.LoopStart, Count = 2 });
                steps.Insert(at + 1, new Step { Kind = StepKind.LoopEnd });
                break;
            case "Command":
                steps.Insert(at, new Step { Kind = StepKind.Command, Hidden = true });
                break;
            case "Open":
                steps.Insert(at, new Step { Kind = StepKind.Open });
                break;
            case "Script":
                // A working starter that shows the convention: what param() declares becomes a field in the editor.
                steps.Insert(at, new Step
                {
                    Kind = StepKind.Script,
                    Hidden = true,
                    Wait = true,
                    Script = "# Copy text to the clipboard\r\nparam(\r\n    [Parameter(HelpMessage = \"Text to copy\")] [string] $Text = \"Hello from Clicket\"\r\n)\r\n\r\nSet-Clipboard -Value $Text\r\n",
                });
                break;
        }
        StepsChanged(new[] { at });
        StepEditor.FocusPrimaryField();
    }

    private void OnWrapLoop(object sender, RoutedEventArgs e)
    {
        var selected = SelectedIndexes();
        if (selected.Count == 0) return;
        PushUndo("wrap", force: true);
        WrapInLoop(selected[0], selected[selected.Count - 1]);
    }

    private void WrapInLoop(int first, int last)
    {
        if (_current == null) return;
        var steps = _current.Macro.Steps;
        steps.Insert(last + 1, new Step { Kind = StepKind.LoopEnd });
        steps.Insert(first, new Step { Kind = StepKind.LoopStart, Count = 2 });
        StepsChanged(new[] { first });
        StepEditor.FocusPrimaryField();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelection(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelection(1);

    /// <summary>Moves the selected block (first to last selected) by one place.</summary>
    private void MoveSelection(int direction)
    {
        if (_current == null) return;
        var selected = SelectedIndexes();
        if (selected.Count == 0) return;
        var first = selected[0];
        var last = selected[selected.Count - 1];
        var steps = _current.Macro.Steps;
        if (direction < 0 && first == 0 || direction > 0 && last >= steps.Count - 1) return;
        PushUndo("move", force: true);
        if (direction < 0)
        {
            var moving = steps[first - 1];
            steps.RemoveAt(first - 1);
            steps.Insert(last, moving);
        }
        else
        {
            var moving = steps[last + 1];
            steps.RemoveAt(last + 1);
            steps.Insert(first, moving);
        }
        StepsChanged(Enumerable.Range(first + direction, last - first + 1));
    }

    private void OnDuplicateStep(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var selected = SelectedIndexes();
        if (selected.Count == 0) return;
        PushUndo("duplicate", force: true);
        var steps = _current.Macro.Steps;
        var copies = selected.Select(i => steps[i].Clone()).ToList();
        var at = selected[selected.Count - 1] + 1;
        steps.InsertRange(at, copies);
        StepsChanged(Enumerable.Range(at, copies.Count));
    }

    private void OnDeleteStep(object sender, RoutedEventArgs e) => DeleteSelectedSteps();

    private void DeleteSelectedSteps()
    {
        if (_current == null) return;
        var selected = SelectedIndexes();
        if (selected.Count == 0) return;
        PushUndo("delete", force: true);
        var steps = _current.Macro.Steps;
        for (var i = selected.Count - 1; i >= 0; i--) steps.RemoveAt(selected[i]);
        var next = Math.Min(selected[0], steps.Count - 1);
        StepsChanged(next >= 0 ? new[] { next } : new int[0]);
    }

    private void CopySteps()
    {
        if (_current == null) return;
        var selected = SelectedIndexes();
        if (selected.Count > 0) _clipboard = selected.Select(i => _current.Macro.Steps[i].Clone()).ToList();
    }

    private void PasteSteps()
    {
        if (_current == null || _clipboard.Count == 0) return;
        PushUndo("paste", force: true);
        var at = InsertionIndex();
        _current.Macro.Steps.InsertRange(at, _clipboard.Select(s => s.Clone()));
        StepsChanged(Enumerable.Range(at, _clipboard.Count));
    }

    private void OnStepsKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Delete) { DeleteSelectedSteps(); e.Handled = true; }
        else if (alt && key == Key.Up) { MoveSelection(-1); e.Handled = true; }
        else if (alt && key == Key.Down) { MoveSelection(1); e.Handled = true; }
        else if (ctrl && key == Key.D) { OnDuplicateStep(sender, e); e.Handled = true; }
        else if (ctrl && key == Key.C) { CopySteps(); e.Handled = true; }
        else if (ctrl && key == Key.V) { PasteSteps(); e.Handled = true; }
        else if (ctrl && key == Key.A) { StepsList.SelectAll(); e.Handled = true; }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Settings.Visibility == Visibility.Visible && !(Keyboard.FocusedElement is KeyCaptureBox))
        {
            Settings.Hide();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Z && !(Keyboard.FocusedElement is TextBox))
        {
            Undo();
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.N && !(Keyboard.FocusedElement is TextBox))
        {
            OnNewMacro(sender, e);
            e.Handled = true;
        }
    }

    // ======================================================== Undo

    /// <summary>Snapshots the step list before a change. Typing in one field within a few seconds counts as one change.</summary>
    private void PushUndo(string group, bool force = false)
    {
        if (_current == null) return;
        var now = DateTime.UtcNow;
        if (!force && group.Length > 0 && group == _lastUndoGroup && now - _lastUndoAt < TimeSpan.FromSeconds(4))
        {
            _lastUndoAt = now;
            return;
        }
        _lastUndoGroup = force ? "" : group;
        _lastUndoAt = now;
        if (!_undo.TryGetValue(_current.Macro.Id, out var stack))
        {
            stack = new Stack<List<Step>>();
            _undo[_current.Macro.Id] = stack;
        }
        stack.Push(_current.Macro.Steps.Select(s => s.Clone()).ToList());
        if (stack.Count > MaxUndo)
        {
            var kept = stack.Take(MaxUndo).Reverse().ToList();
            stack.Clear();
            foreach (var entry in kept) stack.Push(entry);
        }
    }

    private void Undo()
    {
        if (_current == null || !_undo.TryGetValue(_current.Macro.Id, out var stack) || stack.Count == 0) return;
        var selected = SelectedIndexes();
        _current.Macro.Steps = stack.Pop();
        _lastUndoGroup = "";
        StepsChanged(selected.Where(i => i < _current.Macro.Steps.Count).DefaultIfEmpty(Math.Min(0, _current.Macro.Steps.Count - 1)));
    }

    // ======================================================== Toast

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        var duration = SystemParameters.ClientAreaAnimation ? Artwork.Token<Duration>("Motion.Normal") : new Duration(TimeSpan.Zero);
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, duration));
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (s, e) =>
        {
            _toastTimer?.Stop();
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));
        };
        _toastTimer.Start();
    }
}
