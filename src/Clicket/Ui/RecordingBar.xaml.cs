using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Ui;

/// <summary>
/// The bar shown while recording: elapsed time, steps captured so far and Stop. It never takes focus, and the
/// engine ignores clicks on it, so stopping does not end up in the macro.
/// </summary>
public partial class RecordingBar : FloatingWindow
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;

    internal RecordingBar(Hotkey stopHotkey)
    {
        InitializeComponent();
        MakeDragHandle(DragArea);
        Hint.Text = stopHotkey.IsEmpty ? "" : "· " + Strings.Format("RecordingStopHint", stopHotkey.ToString());
        Count.Text = Strings.Format("RecordingSteps", 0);
        // Ticks only while recording, and only to refresh two labels.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (s, e) => Tick(), Dispatcher);
        Closed += (s, e) => _timer.Stop();
    }

    internal event Action? StopRequested;

    /// <summary>Bottom centre of the monitor showing Clicket (or under the pointer).</summary>
    internal void ShowOnScreen(Window? near)
    {
        NativeMethods.Point anchor;
        if (near != null && near.IsVisible && NativeMethods.GetWindowRect(new WindowInteropHelper(near).Handle, out var rect))
            anchor = new NativeMethods.Point { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
        else NativeMethods.GetCursorPos(out anchor);
        var work = NativeMethods.WorkAreaAt(anchor, out _);
        var size = MeasurePhysical(anchor);
        PlaceAt((work.Left + work.Right - size.Width) / 2, work.Bottom - size.Height - 24);
        Show();
        _timer.Start();
    }

    private void Tick()
    {
        var elapsed = _clock.Elapsed;
        Elapsed.Text = ((int)elapsed.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":" + elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
        Count.Text = Strings.Format("RecordingSteps", ((App)Application.Current).RecordedCount);
    }

    private void OnStop(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
}
