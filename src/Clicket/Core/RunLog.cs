using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Clicket.Core;

public enum RunEventKind : byte
{
    Started,
    Step,
    ImageFound,
    ImageMissed,
    ImageTimeout,
    Warning,
    Finished,
}

/// <summary>
/// One thing a run did. A small struct, so a macro clicking a thousand times a second logs without formatting text;
/// the log window formats only the rows it shows.
/// </summary>
public readonly struct RunEvent
{
    public RunEvent(long seq, long ticks, RunEventKind kind, int stepIndex, Step? step, string? text, float score, int x, int y, int value)
    {
        Seq = seq;
        Ticks = ticks;
        Kind = kind;
        StepIndex = stepIndex;
        Step = step;
        Text = text;
        Score = score;
        X = x;
        Y = y;
        Value = value;
    }

    public long Seq { get; }

    /// <summary>Stopwatch timestamp; <see cref="RunLog.TimeOf"/> turns it into wall-clock time.</summary>
    public long Ticks { get; }

    public RunEventKind Kind { get; }

    /// <summary>Position of the step in the macro, or -1.</summary>
    public int StepIndex { get; }

    /// <summary>The step as it ran (the runner's own copy, so later edits do not rewrite history).</summary>
    public Step? Step { get; }

    /// <summary>Warnings: the reason, as Windows or the component reported it.</summary>
    public string? Text { get; }

    /// <summary>Image events: the best score, 0–1.</summary>
    public float Score { get; }

    public int X { get; }
    public int Y { get; }

    /// <summary>Started: passes (0 = until stopped). Image events: search time in ms. Finished: passes made.</summary>
    public int Value { get; }
}

/// <summary>
/// The last 60 seconds of one macro's runs, kept in memory only. Appends prune what fell out of the window, so a macro that
/// runs for hours holds no more than a minute of events, and at most <see cref="Capacity"/> of them.
/// Thread-safe: the runner appends, the log window reads by sequence number without copying.
/// </summary>
public sealed class RunLog
{
    public const int Capacity = 1 << 17;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly long WindowTicks = (long)(Window.TotalSeconds * Stopwatch.Frequency);
    private static readonly long AnchorTicks = Stopwatch.GetTimestamp();
    private static readonly DateTime AnchorTime = DateTime.Now;

    private readonly object _gate = new object();
    private RunEvent[] _ring = new RunEvent[256];
    private long _first; // sequence of the oldest kept event
    private long _next;  // sequence the next event gets
    private long _dropped;

    /// <summary>Sequence of the oldest event still kept.</summary>
    public long First
    {
        get { lock (_gate) return _first; }
    }

    /// <summary>Sequence the next event will get; changes on every append and clear.</summary>
    public long Next
    {
        get { lock (_gate) return _next; }
    }

    /// <summary>Events dropped because more than <see cref="Capacity"/> arrived within the window.</summary>
    public long Dropped
    {
        get { lock (_gate) return _dropped; }
    }

    public static DateTime TimeOf(long ticks) => AnchorTime.AddSeconds((ticks - AnchorTicks) / (double)Stopwatch.Frequency);

    public void Add(RunEventKind kind, int stepIndex = -1, Step? step = null, string? text = null, double score = 0, int x = 0, int y = 0, int value = 0) =>
        Add(Stopwatch.GetTimestamp(), kind, stepIndex, step, text, score, x, y, value);

    /// <summary>Appends at a given timestamp (tests use their own clock).</summary>
    public void Add(long ticks, RunEventKind kind, int stepIndex = -1, Step? step = null, string? text = null, double score = 0, int x = 0, int y = 0, int value = 0)
    {
        lock (_gate)
        {
            // Drop what is older than a minute before the newest event: the log shows the last minute of execution.
            while (_first < _next && ticks - _ring[_first & (_ring.Length - 1)].Ticks > WindowTicks)
            {
                _ring[_first & (_ring.Length - 1)] = default;
                _first++;
            }
            if (_next - _first == _ring.Length)
            {
                if (_ring.Length < Capacity) Grow();
                else
                {
                    _ring[_first & (_ring.Length - 1)] = default;
                    _first++;
                    _dropped++;
                }
            }
            _ring[_next & (_ring.Length - 1)] = new RunEvent(_next, ticks, kind, stepIndex, step, text, (float)score, x, y, value);
            _next++;
        }
    }

    /// <summary>The event with this sequence number, if it is still kept.</summary>
    public bool TryGet(long seq, out RunEvent value)
    {
        lock (_gate)
        {
            if (seq < _first || seq >= _next)
            {
                value = default;
                return false;
            }
            value = _ring[seq & (_ring.Length - 1)];
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring, 0, _ring.Length);
            if (_ring.Length > 256) _ring = new RunEvent[256];
            _first = _next;
            _dropped = 0;
        }
    }

    private void Grow()
    {
        var bigger = new RunEvent[_ring.Length * 2];
        for (var seq = _first; seq < _next; seq++) bigger[seq & (bigger.Length - 1)] = _ring[seq & (_ring.Length - 1)];
        _ring = bigger;
    }
}

/// <summary>One log per macro id, created on first use and kept until the app exits.</summary>
public static class RunLogs
{
    private static readonly ConcurrentDictionary<string, RunLog> Logs = new ConcurrentDictionary<string, RunLog>();

    public static RunLog For(string macroId) => Logs.GetOrAdd(macroId, _ => new RunLog());
}
