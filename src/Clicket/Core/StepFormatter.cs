using System;

namespace Clicket.Core;

/// <summary>What a step says in the list: a title, an optional detail, and how long it takes.</summary>
public static class StepFormatter
{
    public static string Title(Step step)
    {
        switch (step.Kind)
        {
            case StepKind.Key:
                var key = step.Key == 0 ? Strings.Get("DescNone")
                    : step.KeyAction == KeyAction.Press ? KeyNames.Describe(new Hotkey(step.Key, step.Modifiers)) : KeyNames.Name(step.Key);
                return Strings.Format(step.KeyAction == KeyAction.Down ? "DescKeyDown" : step.KeyAction == KeyAction.Up ? "DescKeyUp" : "DescKeyPress", key);
            case StepKind.Mouse:
                return MouseTitle(step);
            case StepKind.Path:
                return Strings.Get("DescPath");
            case StepKind.Text:
                var text = (step.Text ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Replace("\t", " ⇥ ");
                if (text.Length == 0) return Strings.Get("DescTextEmpty");
                return Strings.Format("DescText", text.Length > 60 ? text.Substring(0, 60) + "…" : text);
            case StepKind.Delay:
                return Strings.Get("DescDelay");
            case StepKind.LoopStart:
                return step.Count <= 0 ? Strings.Get("DescLoopForever") : Strings.Format("DescLoop", step.Count);
            case StepKind.LoopEnd:
                return Strings.Get("DescLoopEnd");
            case StepKind.Command:
                return Strings.Format("DescCommand", Shorten(step.Command));
            case StepKind.Script:
                return Strings.Format("DescScript", ScriptSummary(step.Script));
            case StepKind.Open:
                return Strings.Format("DescOpen", Shorten(step.Target));
            default:
                return step.Kind.ToString();
        }
    }

    /// <summary>Time the step takes in milliseconds, for the timing bar; 0 for instant steps.</summary>
    public static long Millis(Step step)
    {
        switch (step.Kind)
        {
            case StepKind.Delay: return Math.Max(step.Ms, step.MsMax);
            case StepKind.Path: return PathCodec.Measure(step.PathData).Micros / 1000;
            case StepKind.Text: return (long)(step.Text?.Length ?? 0) * step.CharDelay;
            default: return 0;
        }
    }

    public static string Timing(Step step)
    {
        switch (step.Kind)
        {
            case StepKind.Delay:
                return step.MsMax > step.Ms ? Strings.Duration(step.Ms) + " – " + Strings.Duration(step.MsMax) : Strings.Duration(step.Ms);
            case StepKind.LoopStart:
                return Strings.Format("LoopPauseTiming", Strings.Duration(step.LoopPauseMs));
            case StepKind.Path:
            case StepKind.Text:
                var ms = Millis(step);
                return ms > 0 ? Strings.Duration(ms) : "";
            default:
                return "";
        }
    }

    public static string ButtonName(MouseButton button) => Strings.Get("Button" + button);

    public static string CategoryKey(StepKind kind)
    {
        switch (kind)
        {
            case StepKind.Key: return "Key";
            case StepKind.Mouse: return "Mouse";
            case StepKind.Path: return "Mouse";
            case StepKind.Text: return "Text";
            case StepKind.Delay: return "Delay";
            case StepKind.LoopStart: case StepKind.LoopEnd: return "Loop";
            case StepKind.Command: return "Command";
            case StepKind.Script: return "Script";
            default: return "Open";
        }
    }

    private static string MouseTitle(Step step)
    {
        var button = ButtonName(step.Button).ToLower(Strings.Culture);
        switch (step.MouseAction)
        {
            case MouseAction.DoubleClick: return Capitalize(Strings.Format("DescDoubleClick", button));
            case MouseAction.Down: return Strings.Format("DescButtonDown", button);
            case MouseAction.Up: return Strings.Format("DescButtonUp", button);
            case MouseAction.MoveTo: return Strings.Format("DescMoveTo", step.X, step.Y);
            case MouseAction.MoveBy: return Strings.Format("DescMoveBy", Signed(step.X), Signed(step.Y));
            case MouseAction.Scroll:
                var notches = Math.Abs(step.Amount) / 120.0;
                var amount = notches.ToString(notches % 1 == 0 ? "0" : "0.##", Strings.Culture);
                if (step.Horizontal) return Strings.Format(step.Amount < 0 ? "DescScrollLeft" : "DescScrollRight", amount);
                return Strings.Format(step.Amount < 0 ? "DescScrollDown" : "DescScrollUp", amount);
            default: return Capitalize(Strings.Format("DescClick", button));
        }
    }

    /// <summary>The script's first comment line names it in the list ("# Rename screenshots"); else its first code line.</summary>
    private static string ScriptSummary(string? script)
    {
        string? firstCode = null;
        foreach (var raw in (script ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#requires", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("#", StringComparison.Ordinal) && !line.StartsWith("#>", StringComparison.Ordinal))
            {
                var comment = line.TrimStart('#').Trim();
                if (comment.Length > 0) return Shorten(comment);
                continue;
            }
            firstCode ??= line;
        }
        return Shorten(firstCode);
    }

    private static string Signed(int value) => value > 0 ? "+" + value : value.ToString(Strings.Culture);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Strings.Culture) + text.Substring(1);

    private static string Shorten(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return Strings.Get("DescNone");
        return value.Length > 48 ? value.Substring(0, 48) + "…" : value;
    }
}
