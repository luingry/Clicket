using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Clicket.Native;

namespace Clicket.Ui;

/// <summary>
/// Base for floating layers that must never take focus from the window the user is working in
/// (the recording bar, the macro pad): no activation, no taskbar button, topmost, dragged by hand.
/// </summary>
public class FloatingWindow : Window
{
    private NativeMethods.Point _grabCursor;
    private NativeMethods.Rect _grabWindow;
    private bool _dragging;

    public FloatingWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(this, System.Windows.Media.TextFormattingMode.Ideal);
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var style = NativeMethods.GetWindowLong(Handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLong(Handle, NativeMethods.GwlExStyle, style | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);
    }

    /// <summary>Drag by an element: manual capture, because DragMove enters a modal loop and needs activation.</summary>
    protected void MakeDragHandle(UIElement handle)
    {
        handle.MouseLeftButtonDown += (s, e) =>
        {
            NativeMethods.GetCursorPos(out _grabCursor);
            NativeMethods.GetWindowRect(Handle, out _grabWindow);
            _dragging = handle.CaptureMouse();
        };
        handle.MouseMove += (s, e) =>
        {
            if (!_dragging) return;
            NativeMethods.GetCursorPos(out var cursor);
            var x = _grabWindow.Left + cursor.X - _grabCursor.X;
            var y = _grabWindow.Top + cursor.Y - _grabCursor.Y;
            var work = NativeMethods.WorkAreaAt(cursor, out _);
            var width = _grabWindow.Right - _grabWindow.Left;
            var height = _grabWindow.Bottom - _grabWindow.Top;
            x = Math.Max(work.Left, Math.Min(x, work.Right - width));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - height));
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, NativeMethods.SwpMoveOnly);
        };
        handle.MouseLeftButtonUp += (s, e) =>
        {
            _dragging = false;
            handle.ReleaseMouseCapture();
            OnDragged();
        };
        handle.LostMouseCapture += (s, e) => _dragging = false;
    }

    protected virtual void OnDragged() { }

    /// <summary>Places the window in physical pixels before it is shown; two passes cover a DPI change on arrival.</summary>
    private protected void PlaceAt(int x, int y)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        for (var pass = 0; pass < 2; pass++) NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, NativeMethods.SwpMoveOnly);
    }

    /// <summary>Content size in physical pixels for the monitor at the point (SizeToContent has not run before Show).</summary>
    private protected (int Width, int Height) MeasurePhysical(NativeMethods.Point at)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        NativeMethods.WorkAreaAt(at, out var scale);
        return ((int)Math.Ceiling(root.DesiredSize.Width * scale), (int)Math.Ceiling(root.DesiredSize.Height * scale));
    }

    protected static Cursor HandCursor => Cursors.Hand;
}
