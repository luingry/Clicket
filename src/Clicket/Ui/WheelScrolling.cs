using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Clicket.Ui;

/// <summary>
/// WPF's ScrollViewer scrolls a fixed step for every wheel message and ignores <see cref="MouseWheelEventArgs.Delta"/>.
/// Anything that sends fractional deltas (smooth-scrolling tools such as SmoothMice, high-resolution wheels, precision
/// touchpads) therefore scrolls the full step per message: many times too far, in jumps. This scales the scroll with the
/// delta (120 = one notch) in every ScrollViewer of the process; whole notches keep WPF's own behaviour.
/// Same fix as SwissTechKnife 0.4.9 (its ERRORS.md, 2026-10-26).
/// </summary>
internal static class WheelScrolling
{
    private const int WheelDelta = 120;
    /// <summary>WPF (net48) scrolls this many device-independent pixels per notch in pixel mode, whatever WheelScrollLines is.</summary>
    private const double PixelsPerNotch = 48;
    private const int FallbackLines = 3;

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new ConditionalWeakTable<ScrollViewer, State>();
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        // Class handlers run before ScrollViewer.OnMouseWheel, so marking the event handled replaces WPF's fixed step.
        // Bubbling reaches the innermost ScrollViewer first, as WPF does.
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel));
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.Handled || e.Delta == 0 || e.Delta % WheelDelta == 0) return;
        if (viewer.ScrollableHeight <= 0 || viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return;

        var notches = -e.Delta / (double)WheelDelta;
        var state = GetState(viewer);
        if (viewer.CanContentScroll)
        {
            var wheelLines = SystemParameters.WheelScrollLines;
            if (wheelLines < 0) return; // "one screen per notch": no meaningful fraction, keep WPF's page scrolling
            ScrollByLines(viewer, state, notches * (wheelLines == 0 ? FallbackLines : wheelLines));
        }
        else
        {
            ScrollByPixels(viewer, state, notches * PixelsPerNotch);
        }
        e.Handled = true;
    }

    private static State GetState(ScrollViewer viewer)
    {
        if (States.TryGetValue(viewer, out var state)) return state;
        state = new State();
        States.Add(viewer, state);
        viewer.ScrollChanged += OnScrollChanged;
        return state;
    }

    /// <summary>Layout applied the requested offset, so VerticalOffset is current again and an older request is stale (after Home, say).</summary>
    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (States.TryGetValue((ScrollViewer)sender, out var state)) state.PendingOffset = double.NaN;
    }

    /// <summary>
    /// Pixel offsets follow the delta exactly. Several messages can arrive before the next layout pass, so they accumulate
    /// on the offset requested last instead of the stale VerticalOffset.
    /// </summary>
    private static void ScrollByPixels(ScrollViewer viewer, State state, double pixels)
    {
        var current = double.IsNaN(state.PendingOffset) ? viewer.VerticalOffset : state.PendingOffset;
        var target = Math.Max(0, Math.Min(viewer.ScrollableHeight, current + pixels));
        viewer.ScrollToVerticalOffset(target);
        state.PendingOffset = target;
    }

    /// <summary>Item scrolling cannot move by a fraction of an item: the remainder carries to the next message.</summary>
    private static void ScrollByLines(ScrollViewer viewer, State state, double lines)
    {
        if (state.Remainder != 0 && Math.Sign(state.Remainder) != Math.Sign(lines)) state.Remainder = 0;
        state.Remainder += lines;
        var whole = (int)state.Remainder;
        state.Remainder -= whole;
        for (var i = 0; i < Math.Abs(whole); i++)
        {
            if (whole > 0) viewer.LineDown();
            else viewer.LineUp();
        }
    }

    private sealed class State
    {
        public double PendingOffset = double.NaN;
        public double Remainder;
    }
}
