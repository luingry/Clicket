using System;
using System.Collections.Generic;

namespace Clicket.Core;

/// <summary>
/// The executable form of a macro's flat step list: every loop start knows its end and vice versa.
/// Built once per run; validation runs the same code, so the editor and the player can never disagree.
/// </summary>
public sealed class MacroProgram
{
    private MacroProgram(IReadOnlyList<Step> steps, int[] partner, int[] depth)
    {
        Steps = steps;
        Partner = partner;
        Depth = depth;
    }

    public IReadOnlyList<Step> Steps { get; }

    /// <summary>For a loop start, the index of its end, and for a loop end, its start; -1 otherwise.</summary>
    public int[] Partner { get; }

    /// <summary>Nesting depth of each step, for indentation.</summary>
    public int[] Depth { get; }

    /// <summary>Pairs loops. Unbalanced markers are reported with the first offending index.</summary>
    public static MacroProgram Build(IReadOnlyList<Step> steps, out int errorIndex)
    {
        var partner = new int[steps.Count];
        var depth = new int[steps.Count];
        var open = new Stack<int>();
        errorIndex = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            partner[i] = -1;
            depth[i] = open.Count;
            switch (steps[i].Kind)
            {
                case StepKind.LoopStart:
                    open.Push(i);
                    break;
                case StepKind.LoopEnd:
                    if (open.Count == 0)
                    {
                        if (errorIndex < 0) errorIndex = i;
                        break;
                    }
                    var start = open.Pop();
                    partner[start] = i;
                    partner[i] = start;
                    depth[i] = open.Count;
                    break;
            }
        }
        if (open.Count > 0 && errorIndex < 0)
        {
            var first = int.MaxValue;
            foreach (var index in open) first = Math.Min(first, index);
            errorIndex = first;
        }
        return new MacroProgram(steps, partner, depth);
    }

    /// <summary>Approximate time of one pass in milliseconds, or null when a loop repeats forever.</summary>
    public static long? EstimateMillis(IReadOnlyList<Step> steps)
    {
        var program = Build(steps, out var error);
        if (error >= 0) return null;
        return Estimate(program, 0, steps.Count);
    }

    private static long? Estimate(MacroProgram program, int from, int to)
    {
        long total = 0;
        for (var i = from; i < to; i++)
        {
            var step = program.Steps[i];
            switch (step.Kind)
            {
                case StepKind.Delay:
                    total += step.MsMax > step.Ms ? (step.Ms + step.MsMax) / 2 : step.Ms;
                    break;
                case StepKind.Path:
                    total += PathCodec.Measure(step.PathData).Micros / 1000;
                    break;
                case StepKind.Text:
                    total += (long)(step.Text?.Length ?? 0) * step.CharDelay;
                    break;
                case StepKind.LoopStart:
                    var end = program.Partner[i];
                    if (step.Count <= 0) return null;
                    var inner = Estimate(program, i + 1, end);
                    if (inner == null) return null;
                    total += inner.Value * step.Count;
                    i = end;
                    break;
            }
        }
        return total;
    }
}
