using System;
using System.Collections.Generic;

namespace Clicket.Core;

public enum RawKind { KeyDown, KeyUp, ButtonDown, ButtonUp, Wheel, HWheel, Move }

/// <summary>One physical input event as the hook saw it, timed in microseconds.</summary>
public struct RawEvent
{
    public RawKind Kind;
    public int Vk, Scan;
    public bool Extended;
    public MouseButton Button;
    public int X, Y, WheelDelta;
    public long Micros;
}

public enum DelayMode { Recorded, Fixed, None }

public sealed class RecordingOptions
{
    public bool Keyboard { get; set; } = true;
    public bool Buttons { get; set; } = true;
    public bool Movement { get; set; }
    public DelayMode Delays { get; set; } = DelayMode.Recorded;
    public int FixedDelayMs { get; set; } = 50;
}

/// <summary>
/// Turns raw input into steps. Pure, so its rules are unit-tested:
/// key auto-repeat collapses into one press, ups without a recorded down are dropped (keys already held at the start),
/// downs never released at the end are dropped (the stop hotkey's modifiers), and pointer motion between two events
/// becomes one lossless path step that replays on its own clock.
/// </summary>
public sealed class RecordingBuilder
{
    private readonly RecordingOptions _options;
    private readonly List<Step> _steps = new List<Step>();
    private readonly HashSet<int> _heldKeys = new HashSet<int>();
    private readonly HashSet<MouseButton> _heldButtons = new HashSet<MouseButton>();
    private readonly List<PathPoint> _path = new List<PathPoint>();
    private long _pathStart;
    private long _lastEvent = -1;
    private long _carryMicros;
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private int _emittedX = int.MinValue, _emittedY = int.MinValue;

    public RecordingBuilder(RecordingOptions options) => _options = options;

    /// <summary>Steps so far, counting the motion in progress as one, so a movement-only recording shows up as it happens.</summary>
    public int Count => _steps.Count + (_path.Count > 0 ? 1 : 0);

    public void Add(RawEvent e)
    {
        switch (e.Kind)
        {
            case RawKind.Move:
                AddMove(e);
                return;
            case RawKind.KeyDown:
                if (!_options.Keyboard || !_heldKeys.Add(e.Vk)) return; // auto-repeat
                Emit(e.Micros, Step.KeyStep(e.Vk, e.Scan, e.Extended, KeyAction.Down));
                return;
            case RawKind.KeyUp:
                if (!_options.Keyboard || !_heldKeys.Remove(e.Vk)) return;
                Emit(e.Micros, Step.KeyStep(e.Vk, e.Scan, e.Extended, KeyAction.Up));
                return;
            case RawKind.ButtonDown:
                if (!_options.Buttons || !_heldButtons.Add(e.Button)) return;
                EmitPositioned(e, Step.MouseStep(MouseAction.Down, e.Button));
                return;
            case RawKind.ButtonUp:
                if (!_options.Buttons || !_heldButtons.Remove(e.Button)) return;
                EmitPositioned(e, Step.MouseStep(MouseAction.Up, e.Button));
                return;
            case RawKind.Wheel:
            case RawKind.HWheel:
                if (!_options.Buttons || e.WheelDelta == 0) return;
                var scroll = Step.MouseStep(MouseAction.Scroll);
                scroll.Amount = e.WheelDelta;
                scroll.Horizontal = e.Kind == RawKind.HWheel;
                EmitPositioned(e, scroll);
                return;
        }
    }

    /// <summary>
    /// The finished step list. Motion since the last click or key is kept: a recording may be motion alone.
    /// <paramref name="trimTrailing"/> (stopping from Clicket's own bar) drops the trailing samples it accepts, the
    /// pointer resting over Clicket's windows, without touching the motion made before it.
    /// </summary>
    public List<Step> Finish(Func<int, int, bool>? trimTrailing = null)
    {
        if (trimTrailing != null)
        {
            while (_path.Count > 0 && trimTrailing(_path[_path.Count - 1].X, _path[_path.Count - 1].Y)) _path.RemoveAt(_path.Count - 1);
        }
        FlushPath();

        // Downs never released (the stop hotkey's modifiers, a drag released over Clicket) would leave the macro holding
        // them; each one still held goes, wherever it sits (pointer motion may have landed after it).
        for (var i = _steps.Count - 1; i >= 0; i--)
        {
            var step = _steps[i];
            var heldKey = step.Kind == StepKind.Key && step.KeyAction == KeyAction.Down && _heldKeys.Remove(step.Key);
            var heldButton = step.Kind == StepKind.Mouse && step.MouseAction == MouseAction.Down && _heldButtons.Remove(step.Button);
            if (heldKey || heldButton) _steps.RemoveAt(i);
        }
        while (_steps.Count > 0 && _steps[_steps.Count - 1].Kind == StepKind.Delay) _steps.RemoveAt(_steps.Count - 1);
        return new List<Step>(_steps);
    }

    private void AddMove(RawEvent e)
    {
        if (e.X == _lastX && e.Y == _lastY) return;
        _lastX = e.X;
        _lastY = e.Y;
        if (!_options.Movement) return;
        if (_path.Count == 0)
        {
            // Motion starts where the previous event left off; the wait before the first sample is part of the path.
            _pathStart = _lastEvent < 0 ? e.Micros : _lastEvent;
        }
        _path.Add(new PathPoint(e.X, e.Y, Math.Max(0, e.Micros - _pathStart)));
    }

    private void EmitPositioned(RawEvent e, Step step)
    {
        _lastX = e.X;
        _lastY = e.Y;
        if ((!_options.Movement || _path.Count == 0) && (e.X != _emittedX || e.Y != _emittedY))
        {
            // Without recorded motion before it (motion off, or a click before the pointer moved) a click still lands where it happened.
            var move = Step.MouseStep(MouseAction.MoveTo);
            move.X = e.X;
            move.Y = e.Y;
            Emit(e.Micros, move);
            _emittedX = e.X;
            _emittedY = e.Y;
            _steps.Add(step);
            return;
        }
        Emit(e.Micros, step);
        _emittedX = e.X;
        _emittedY = e.Y;
    }

    private void Emit(long micros, Step step)
    {
        FlushPath();
        if (_lastEvent >= 0)
        {
            switch (_options.Delays)
            {
                case DelayMode.Recorded:
                    var gap = micros - _lastEvent + _carryMicros;
                    var ms = gap / 1000;
                    _carryMicros = gap - ms * 1000;
                    if (ms > 0) _steps.Add(Step.DelayStep((int)Math.Min(int.MaxValue, ms)));
                    break;
                case DelayMode.Fixed:
                    if (_options.FixedDelayMs > 0) _steps.Add(Step.DelayStep(_options.FixedDelayMs));
                    break;
            }
        }
        _steps.Add(step);
        _lastEvent = micros;
    }

    private void FlushPath()
    {
        if (_path.Count == 0) return;
        var last = _path[_path.Count - 1];
        _steps.Add(new Step { Kind = StepKind.Path, PathData = PathCodec.Encode(_path) });
        _emittedX = last.X;
        _emittedY = last.Y;
        // The path consumed its own time; the next delay counts from its last sample.
        _lastEvent = _pathStart + last.Micros;
        _carryMicros = 0;
        _path.Clear();
    }
}
