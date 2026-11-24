using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Ui;

/// <summary>
/// The last minute of one macro's execution, live. Events stay in the macro's <see cref="RunLog"/> whether or not this
/// window is open; the window reads them by sequence number and formats only the rows on screen, so even a macro that
/// clicks a thousand times a second scrolls smoothly. While it is open it checks for new events four times a second.
/// </summary>
public partial class RunLogWindow : Window
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(250);

    private readonly Macro _macro;
    private readonly RunLog _log;
    private readonly DispatcherTimer _refresh;
    private long _shownFirst = -1, _shownNext = -1;
    private ScrollViewer? _scroller;
    private DispatcherTimer? _statusTimer;

    public RunLogWindow(Macro macro)
    {
        InitializeComponent();
        _macro = macro;
        _log = RunLogs.For(macro.Id);
        Icon = Artwork.Render(32, 32, dc => Artwork.DrawAppIcon(dc, 32));
        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshEvery };
        _refresh.Tick += (s, e) => Refresh(force: false);

        SourceInitialized += (s, e) => NativeMethods.StyleFrame(new WindowInteropHelper(this).Handle);
        StateChanged += (s, e) =>
        {
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        };
        Loaded += (s, e) =>
        {
            _scroller = FindScroller(Events);
            Refresh(force: true);
            _refresh.Start();
        };
        Closed += (s, e) =>
        {
            _refresh.Stop();
            _statusTimer?.Stop();
            Strings.LanguageChanged -= OnLanguageChanged;
        };
        Strings.LanguageChanged += OnLanguageChanged;
        UpdateTitle();
    }

    /// <summary>Opens beside the editor's right edge, inside its bounds, or centred when the editor is hidden.</summary>
    public void ShowNear(Window owner)
    {
        if (owner.IsVisible && owner.WindowState == WindowState.Normal)
        {
            Left = Math.Max(owner.Left, owner.Left + owner.ActualWidth - Width - 24);
            Top = owner.Top + 96;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        Show();
        Activate();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        UpdateTitle();
        Refresh(force: true);
    }));

    private void UpdateTitle()
    {
        var name = _macro.Name.Length == 0 ? Strings.Get("NewMacroName") : _macro.Name;
        Title = Strings.Format("RunLogTitle", name);
        TitleText.Text = Title;
    }

    private void Refresh(bool force)
    {
        var first = _log.First;
        var next = _log.Next;
        if (!force && first == _shownFirst && next == _shownNext) return;

        // Follow the newest event when the list was at the bottom; otherwise keep the rows the user is reading in place.
        var offset = _scroller?.VerticalOffset ?? 0;
        var atBottom = _scroller == null || _scroller.VerticalOffset >= _scroller.ScrollableHeight - 1;
        var shift = _shownFirst < 0 ? 0 : first - _shownFirst;

        _shownFirst = first;
        _shownNext = next;
        var count = (int)Math.Max(0, next - first);
        Events.ItemsSource = new LogView(_log, first, count);
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_statusTimer?.IsEnabled != true) UpdateCount(count);

        if (_scroller == null || count == 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (atBottom) _scroller.ScrollToEnd();
            else _scroller.ScrollToVerticalOffset(Math.Max(0, offset - shift));
        }), DispatcherPriority.Loaded);
    }

    private void UpdateCount(int count)
    {
        var dropped = _log.Dropped;
        CountText.Text = count == 0 ? "" : Strings.Format("RunLogCount", count.ToString("N0", Strings.Culture))
            + (dropped > 0 ? Strings.Format("RunLogDropped", dropped.ToString("N0", Strings.Culture)) : "");
    }

    private void ShowStatus(string text)
    {
        CountText.Text = text;
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _statusTimer.Tick += (s, e) =>
        {
            _statusTimer!.Stop();
            UpdateCount((int)Math.Max(0, _shownNext - _shownFirst));
        };
        _statusTimer.Start();
    }

    private void OnPin(object sender, RoutedEventArgs e) => Topmost = PinToggle.IsChecked == true;

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        Refresh(force: true);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        var lines = 0;
        for (var seq = _log.First; seq < _log.Next; seq++)
        {
            if (!_log.TryGet(seq, out var item)) continue;
            var row = LogRow.From(item);
            text.Append(row.Time).Append('\t').Append(row.Number).Append('\t').Append(row.Text).Append("\r\n");
            lines++;
        }
        if (lines == 0) return;
        try
        {
            Clipboard.SetText(text.ToString());
            ShowStatus(Strings.Format("RunLogCopied", lines.ToString("N0", Strings.Culture)));
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            Log.Warn("Copying the run log failed: " + error.Message); // the clipboard was held by another app
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScroller(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// A read-only window onto the log by sequence number: the virtualizing list asks only for the rows it shows, and each
    /// row is formatted then. Rows already handed out keep their identity until the next refresh.
    /// </summary>
    private sealed class LogView : IList
    {
        private readonly RunLog _log;
        private readonly long _first;
        private readonly Dictionary<int, LogRow> _made = new Dictionary<int, LogRow>();

        public LogView(RunLog log, long first, int count)
        {
            _log = log;
            _first = first;
            Count = count;
        }

        public int Count { get; }

        public object? this[int index]
        {
            get
            {
                if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                if (_made.TryGetValue(index, out var row)) return row;
                // Pruned since this view was made (more than a minute ago): an empty row until the next refresh.
                row = _log.TryGet(_first + index, out var item) ? LogRow.From(item) : LogRow.Gone(_first + index);
                if (_made.Count > 4096) _made.Clear();
                _made[index] = row;
                return row;
            }
            set => throw new NotSupportedException();
        }

        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public object SyncRoot => this;
        public bool IsSynchronized => false;

        public int IndexOf(object? value) => value is LogRow row && row.Seq >= _first && row.Seq < _first + Count ? (int)(row.Seq - _first) : -1;
        public bool Contains(object? value) => IndexOf(value) >= 0;

        public IEnumerator GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }

        public void CopyTo(Array array, int index)
        {
            for (var i = 0; i < Count; i++) array.SetValue(this[i], index + i);
        }

        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}

/// <summary>One event as the log window shows it.</summary>
public sealed class LogRow
{
    private LogRow(long seq) => Seq = seq;

    public long Seq { get; }
    public string Time { get; private set; } = "";
    public string Number { get; private set; } = "";
    public string Text { get; private set; } = "";
    public string Glyph { get; private set; } = "";
    public Brush Hue { get; private set; } = Brushes.Transparent;
    public Brush HueSoft { get; private set; } = Brushes.Transparent;

    /// <summary>Routine noise (an image not seen yet): drawn in a quieter tone.</summary>
    public bool IsQuiet { get; private set; }

    public bool IsWarning { get; private set; }

    public static LogRow Gone(long seq) => new LogRow(seq);

    public static LogRow From(RunEvent e)
    {
        var row = new LogRow(e.Seq)
        {
            Time = RunLog.TimeOf(e.Ticks).ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
            Number = e.StepIndex >= 0 ? "#" + (e.StepIndex + 1).ToString(Strings.Culture) : "",
        };
        var family = e.Step == null ? null : e.Step.Kind == StepKind.Path ? "Path" : StepFormatter.CategoryKey(e.Step.Kind);
        switch (e.Kind)
        {
            case RunEventKind.Started:
                row.Text = e.Value == 0 ? Strings.Get("LogStartedEndless") : e.Value == 1 ? Strings.Get("LogStarted") : Strings.Format("LogStartedPasses", e.Value);
                row.Paint("Glyph.Play", "Brush.Accent", "Brush.Accent.Soft");
                break;
            case RunEventKind.Finished:
                row.Text = Strings.Format("LogFinished", e.Value.ToString("N0", Strings.Culture));
                row.Paint("Glyph.Stop", "Brush.Text.Secondary", "Brush.Overlay.Pressed");
                break;
            case RunEventKind.Step:
                row.Text = e.Step == null ? "" : StepFormatter.Title(e.Step);
                row.PaintFamily(family);
                break;
            case RunEventKind.ImageFound:
                row.Text = Strings.Format(e.Text == "Click" ? "LogImageClicked" : "LogImageFound", StepEditor.Percent(e.Score), e.X, e.Y, Strings.Duration(e.Value));
                row.PaintFamily("Image");
                break;
            case RunEventKind.ImageMissed:
                row.Text = Strings.Format("LogImageMissed", StepEditor.Percent(e.Score), Strings.Duration(e.Value));
                row.PaintFamily("Image");
                row.IsQuiet = true;
                break;
            case RunEventKind.ImageTimeout:
                row.Text = Strings.Format("LogImageTimeout", Strings.Duration(e.Value));
                row.PaintFamily("Image");
                break;
            default:
                row.Text = e.Text == "Unmatched" ? Strings.Get("LogUnmatched")
                    : e.Text == "NoPicture" ? Strings.Get("LogNoPicture")
                    : Strings.Format("LogWarning", e.Text ?? "");
                row.Paint("Glyph.Warning", "Brush.Warning", "Brush.Warning.Soft");
                row.IsWarning = true;
                break;
        }
        return row;
    }

    private void PaintFamily(string? family)
    {
        if (family == null) return;
        var hue = family == "Path" ? "Mouse" : family;
        Paint("Glyph." + family, "Brush.Cat." + hue, "Brush.Cat." + hue + ".Soft");
    }

    private void Paint(string glyph, string hue, string soft)
    {
        Glyph = Artwork.Token<string>(glyph);
        Hue = Artwork.Token<Brush>(hue);
        HueSoft = Artwork.Token<Brush>(soft);
    }
}
