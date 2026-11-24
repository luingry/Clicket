using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Ui;

/// <summary>
/// A clear veil over every monitor that reports where the user clicks, in the same physical pixels the player uses.
/// Clicket's own window steps aside while it is open, so the target app is visible underneath.
/// </summary>
internal sealed class PositionPicker : Window
{
    private readonly Action<NativeMethods.Point> _done;
    private readonly Window? _owner;
    private readonly Border _badge;
    private readonly TextBlock _coords;
    private bool _finished;

    private PositionPicker(Window? owner, Action<NativeMethods.Point> done)
    {
        _owner = owner;
        _done = done;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Artwork.Token<Brush>("Brush.PickVeil");
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;

        var hint = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0), Padding = new Thickness(14, 9, 14, 9) };
        hint.SetResourceReference(Border.BackgroundProperty, "Brush.Surface4");
        hint.SetResourceReference(Border.CornerRadiusProperty, "Radius.Md");
        hint.SetResourceReference(EffectProperty, "Elevation.Popup");
        var hintText = new TextBlock { Text = Strings.Get("PickHint") };
        hintText.SetResourceReference(StyleProperty, "Text.Body");
        hint.Child = hintText;

        _coords = new TextBlock();
        _coords.SetResourceReference(StyleProperty, "Text.BodyStrong");
        _badge = new Border { Padding = new Thickness(8, 4, 8, 4), IsHitTestVisible = false, Child = _coords };
        _badge.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        _badge.SetResourceReference(Border.CornerRadiusProperty, "Radius.Sm");
        _coords.SetResourceReference(TextBlock.ForegroundProperty, "Brush.OnAccent");

        var canvas = new Canvas();
        canvas.Children.Add(_badge);
        var root = new Grid();
        root.Children.Add(canvas);
        root.Children.Add(hint);
        Content = root;

        SourceInitialized += (s, e) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var x = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen);
            var y = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen);
            var w = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
            var h = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);
            for (var pass = 0; pass < 2; pass++) NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, w, h, 0x0010);
        };
        MouseMove += (s, e) => Track(e.GetPosition(canvas));
        MouseLeftButtonDown += (s, e) =>
        {
            NativeMethods.GetCursorPos(out var point);
            Finish(point);
        };
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) Finish(null);
        };
        Deactivated += (s, e) => Finish(null);
    }

    public static void Pick(Window? owner, Action<NativeMethods.Point> done)
    {
        owner?.Hide();
        var picker = new PositionPicker(owner, done);
        picker.Show();
        picker.Activate();
    }

    private void Track(Point local)
    {
        NativeMethods.GetCursorPos(out var point);
        _coords.Text = point.X.ToString(CultureInfo.InvariantCulture) + ", " + point.Y.ToString(CultureInfo.InvariantCulture);
        Canvas.SetLeft(_badge, local.X + 16);
        Canvas.SetTop(_badge, local.Y + 18);
    }

    private void Finish(NativeMethods.Point? point)
    {
        if (_finished) return;
        _finished = true;
        Close();
        if (_owner != null)
        {
            _owner.Show();
            _owner.Activate();
        }
        if (point.HasValue) _done(point.Value);
    }
}
