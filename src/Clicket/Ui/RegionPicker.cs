using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clicket.Core;
using Clicket.Input;
using Clicket.Native;

namespace Clicket.Ui;

/// <summary>Runs screen work with Clicket's window out of the picture.</summary>
internal static class ScreenGrab
{
    /// <summary>A hidden window leaves the screen on the next compositor frame; this pause keeps it out of the capture.</summary>
    private static readonly TimeSpan HideSettle = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// Hides <paramref name="owner"/>, runs <paramref name="work"/> on a worker thread in physical pixels, then calls
    /// <paramref name="done"/> on the UI thread (null result when the work threw). With <paramref name="restore"/> the owner
    /// comes back first.
    /// </summary>
    public static void WithoutOwner<T>(Window? owner, Func<T> work, Action<T?> done, bool restore = true) where T : class
    {
        var hidden = owner != null && owner.IsVisible;
        if (hidden) owner!.Hide();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer { Interval = HideSettle };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            new Thread(() =>
            {
                NativeMethods.UsePhysicalPixels();
                T? result = null;
                try { result = work(); }
                catch (Exception error) { Log.Error("Screen work failed.", error); }
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (hidden && restore)
                    {
                        owner!.Show();
                        owner.Activate();
                    }
                    done(result);
                }));
            }) { IsBackground = true, Name = "Clicket screen grab" }.Start();
        };
        timer.Start();
    }
}

/// <summary>
/// Freezes the screen and lets the user drag around the part to search for. The selection is cut from the frozen copy in
/// physical pixels, exactly as the image step will see the screen. Esc, a right click or switching away cancels.
/// </summary>
internal sealed class RegionPicker : Window
{
    private const int MinSide = 4;

    private readonly PixelImage _screen;
    private readonly int _left, _top;
    private readonly Window? _owner;
    private readonly Action<PixelImage> _done;
    private readonly Path _veil;
    private readonly Rectangle _outline;
    private readonly Border _hint;
    private readonly RectangleGeometry _whole = new RectangleGeometry();
    private readonly RectangleGeometry _hole = new RectangleGeometry();
    private Point _startLocal;
    private NativeMethods.Point _startScreen;
    private bool _dragging, _finished;

    private RegionPicker(Window? owner, PixelImage screen, int left, int top, Action<PixelImage> done)
    {
        _owner = owner;
        _screen = screen;
        _left = left;
        _top = top;
        _done = done;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        Background = Brushes.Black;

        var frozen = new Image { Source = ImageCodec.ToSource(screen), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(frozen, BitmapScalingMode.NearestNeighbor);

        _veil = new Path { Data = new CombinedGeometry(GeometryCombineMode.Exclude, _whole, _hole), IsHitTestVisible = false };
        _veil.SetResourceReference(Shape.FillProperty, "Brush.RegionVeil");

        _outline = new Rectangle { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _outline.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        _outline.SetResourceReference(Shape.StrokeThicknessProperty, "Size.RegionStroke");

        _hint = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0), Padding = new Thickness(14, 9, 14, 9), IsHitTestVisible = false };
        _hint.SetResourceReference(Border.BackgroundProperty, "Brush.Surface4");
        _hint.SetResourceReference(Border.CornerRadiusProperty, "Radius.Md");
        _hint.SetResourceReference(EffectProperty, "Elevation.Popup");
        var hintText = new TextBlock { Text = Strings.Get("RegionHint") };
        hintText.SetResourceReference(StyleProperty, "Text.Body");
        _hint.Child = hintText;

        var canvas = new Canvas();
        canvas.Children.Add(_outline);
        var root = new Grid();
        root.Children.Add(frozen);
        root.Children.Add(_veil);
        root.Children.Add(canvas);
        root.Children.Add(_hint);
        Content = root;
        root.SizeChanged += (s, e) => _whole.Rect = new Rect(e.NewSize);

        SourceInitialized += (s, e) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            for (var pass = 0; pass < 2; pass++) NativeMethods.SetWindowPos(handle, IntPtr.Zero, _left, _top, _screen.Width, _screen.Height, 0x0010);
        };
        MouseLeftButtonDown += (s, e) =>
        {
            _startLocal = e.GetPosition(root);
            NativeMethods.GetCursorPos(out _startScreen);
            _dragging = CaptureMouse();
            _hint.Visibility = Visibility.Collapsed;
            Track(_startLocal);
        };
        MouseMove += (s, e) =>
        {
            if (_dragging) Track(e.GetPosition(root));
        };
        MouseLeftButtonUp += (s, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            NativeMethods.GetCursorPos(out var end);
            var x0 = Math.Min(_startScreen.X, end.X) - _left;
            var y0 = Math.Min(_startScreen.Y, end.Y) - _top;
            var w = Math.Abs(end.X - _startScreen.X) + 1;
            var h = Math.Abs(end.Y - _startScreen.Y) + 1;
            if (w < MinSide || h < MinSide)
            {
                ClearSelection();
                return;
            }
            Finish(_screen.Crop(x0, y0, w, h));
        };
        MouseRightButtonDown += (s, e) => Finish(null);
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) Finish(null);
        };
        Deactivated += (s, e) => Finish(null);
    }

    /// <summary>Hides <paramref name="owner"/>, freezes the screen, and reports the dragged region (nothing when cancelled).</summary>
    public static void Pick(Window? owner, Action<PixelImage> done)
    {
        ScreenGrab.WithoutOwner(owner, () =>
        {
            using var capture = new ScreenCapture();
            var screen = capture.Grab();
            return new Tuple<PixelImage, int, int>(screen, capture.Left, capture.Top);
        }, grabbed =>
        {
            if (grabbed == null)
            {
                Restore(owner);
                return;
            }
            var picker = new RegionPicker(owner, grabbed.Item1, grabbed.Item2, grabbed.Item3, done);
            picker.Show();
            picker.Activate();
        }, restore: false);
    }

    private void Track(Point now)
    {
        var rect = new Rect(_startLocal, now);
        _hole.Rect = rect;
        _outline.Width = Math.Max(1, rect.Width);
        _outline.Height = Math.Max(1, rect.Height);
        Canvas.SetLeft(_outline, rect.Left);
        Canvas.SetTop(_outline, rect.Top);
        _outline.Visibility = Visibility.Visible;
    }

    private void ClearSelection()
    {
        _hole.Rect = Rect.Empty;
        _outline.Visibility = Visibility.Collapsed;
        _hint.Visibility = Visibility.Visible;
    }

    private void Finish(PixelImage? region)
    {
        if (_finished) return;
        _finished = true;
        Close();
        Restore(_owner);
        if (region != null) _done(region);
    }

    private static void Restore(Window? owner)
    {
        if (owner == null) return;
        owner.Show();
        owner.Activate();
    }
}
