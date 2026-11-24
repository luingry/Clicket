using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Clicket.Core;
using Clicket.Native;

namespace Clicket.Ui;

/// <summary>
/// A small always-on-top panel of macros the user pinned. It never activates, so a click plays the macro straight
/// into the window that already has focus; clicking a playing macro stops it.
/// </summary>
public partial class PadWindow : FloatingWindow
{
    private readonly ObservableCollection<MacroItem> _items = new ObservableCollection<MacroItem>();

    public PadWindow()
    {
        InitializeComponent();
        Logo.Source = Artwork.AppIconImage();
        Items.ItemsSource = _items;
        MakeDragHandle(DragArea);
        Refresh();
    }

    private static App Host => (App)Application.Current;

    internal void ShowNear(AppSettings settings)
    {
        NativeMethods.GetCursorPos(out var cursor);
        var size = MeasurePhysical(cursor);
        var anchor = settings.PadX == int.MinValue ? cursor : new NativeMethods.Point { X = settings.PadX, Y = settings.PadY };
        var work = NativeMethods.WorkAreaAt(anchor, out _);
        var x = settings.PadX == int.MinValue ? work.Right - size.Width - 24 : settings.PadX;
        var y = settings.PadY == int.MinValue ? work.Top + 120 : settings.PadY;
        x = Math.Max(work.Left, Math.Min(x, work.Right - size.Width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - size.Height));
        PlaceAt(x, y);
        Show();
    }

    internal void Refresh()
    {
        _items.Clear();
        foreach (var macro in Host.Library.Where(m => m.OnPad))
            _items.Add(new MacroItem(macro) { IsRunning = Host.Engine?.IsRunning(macro.Id) == true });
        Empty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetRunning(string id, bool running)
    {
        foreach (var item in _items.Where(i => i.Macro.Id == id)) item.IsRunning = running;
    }

    protected override void OnDragged()
    {
        if (!NativeMethods.GetWindowRect(Handle, out var rect)) return;
        Host.Settings.PadX = rect.Left;
        Host.Settings.PadY = rect.Top;
        Host.SaveSettings();
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (!(((Button)sender).Tag is MacroItem item) || Host.Engine == null) return;
        if (item.IsRunning)
        {
            Host.Engine.Stop(item.Macro.Id);
            return;
        }
        var macro = item.Macro;
        var passes = macro.Mode == PlayMode.Repeat ? macro.RepeatCount : macro.Mode == PlayMode.Toggle ? 0 : 1;
        Host.Engine.Play(macro, 0, passes);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
