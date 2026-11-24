using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Clicket.Native;

namespace Clicket.Input;

/// <summary>
/// Waits until a Stopwatch deadline with a high-resolution waitable timer, cancellable at any moment.
/// Sleep follows the 15.6 ms tick and pool timers measured 17 ms for 4 ms; this timer lands within about half a millisecond,
/// and the last few hundred microseconds are spun so recorded paths replay on their original clock.
/// </summary>
internal sealed class PreciseWaiter : IDisposable
{
    private static readonly long SpinTicks = Stopwatch.Frequency / 2500; // 400 µs
    private readonly TimerHandle _timer;
    private readonly WaitHandle[] _handles;

    public PreciseWaiter(WaitHandle cancel)
    {
        var handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, NativeMethods.CreateWaitableTimerHighResolution, NativeMethods.TimerAllAccess);
        if (handle == IntPtr.Zero) handle = NativeMethods.CreateWaitableTimerEx(IntPtr.Zero, null, 0, NativeMethods.TimerAllAccess); // before Windows 10 1803
        if (handle == IntPtr.Zero) throw new InvalidOperationException("CreateWaitableTimerEx failed.");
        _timer = new TimerHandle(handle);
        _handles = new[] { cancel, _timer };
    }

    /// <summary>Completed waits and how late they woke, for the per-run log line.</summary>
    public long Waits { get; private set; }
    public long LateTicks { get; private set; }
    public long MaxLateTicks { get; private set; }

    /// <summary>False when cancelled before the deadline.</summary>
    public bool WaitUntil(long deadlineTicks)
    {
        while (true)
        {
            var remaining = deadlineTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                if (_handles[0].WaitOne(0)) return false;
                Record(-remaining);
                return true;
            }
            if (remaining <= SpinTicks)
            {
                if (_handles[0].WaitOne(0)) return false;
                while (Stopwatch.GetTimestamp() < deadlineTicks) Thread.SpinWait(20);
                Record(Stopwatch.GetTimestamp() - deadlineTicks);
                return true;
            }
            // Wake a little early and spin the rest; 100 ns units, negative means relative.
            var due = -(long)((remaining - SpinTicks) * 10_000_000.0 / Stopwatch.Frequency);
            if (due >= 0) due = -1;
            if (!NativeMethods.SetWaitableTimer(_timer.SafeWaitHandle.DangerousGetHandle(), ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                if (_handles[0].WaitOne(1)) return false; // degraded but still cancellable
                continue;
            }
            if (WaitHandle.WaitAny(_handles) == 0) return false;
        }
    }

    public bool Wait(TimeSpan duration) => WaitUntil(Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));

    public void Dispose() => _timer.Dispose();

    private void Record(long late)
    {
        Waits++;
        LateTicks += late;
        if (late > MaxLateTicks) MaxLateTicks = late;
    }

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }
}
