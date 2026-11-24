using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Clicket.Core;

public enum StepKind { Key, Mouse, Path, Text, Delay, LoopStart, LoopEnd, Command, Open, Script, ImageStart, ImageEnd }

/// <summary>A value the user set for one of a script's parameters.</summary>
[DataContract]
public sealed class ScriptArg
{
    [DataMember(Name = "name", Order = 0)] public string Name { get; set; } = "";
    [DataMember(Name = "value", Order = 1)] public string Value { get; set; } = "";
}

public enum KeyAction { Press, Down, Up }

public enum MouseAction { Click, DoubleClick, Down, Up, MoveTo, MoveBy, Scroll }

public enum MouseButton { Left, Right, Middle, X1, X2 }

public enum ShellKind { Cmd, PowerShell }

/// <summary>How a hotkey runs its macro.</summary>
public enum PlayMode
{
    /// <summary>Runs the steps once; pressing again while running is ignored.</summary>
    Once,
    /// <summary>Runs the steps a set number of times.</summary>
    Repeat,
    /// <summary>Repeats while the hotkey is held and stops on release.</summary>
    WhileHeld,
    /// <summary>The first press repeats until the next press.</summary>
    Toggle,
}

[Flags]
public enum Modifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>A key or mouse button plus modifiers. Mouse buttons use their virtual-key codes (4 middle, 5 X1, 6 X2).</summary>
public readonly struct Hotkey : IEquatable<Hotkey>
{
    public Hotkey(int key, Modifiers modifiers)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public int Key { get; }
    public Modifiers Modifiers { get; }
    public bool IsEmpty => Key == 0;
    public bool IsMouse => Key == Vk.MButton || Key == Vk.XButton1 || Key == Vk.XButton2;

    public bool Equals(Hotkey other) => Key == other.Key && Modifiers == other.Modifiers;
    public override bool Equals(object? obj) => obj is Hotkey other && Equals(other);
    public override int GetHashCode() => (Key << 4) | (int)Modifiers;
    public static bool operator ==(Hotkey a, Hotkey b) => a.Equals(b);
    public static bool operator !=(Hotkey a, Hotkey b) => !a.Equals(b);

    public override string ToString() => KeyNames.Describe(this);

    /// <summary>"ctrl+alt+0x70" style, stable across languages.</summary>
    public string Serialize() => IsEmpty ? "" : ((int)Modifiers).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Key.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var parts = text!.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var mods) || !int.TryParse(parts[1], out var key)) return default;
        if (key <= 0 || key > 254) return default;
        return new Hotkey(key, (Modifiers)(mods & 0xF));
    }
}

[DataContract]
public sealed class Macro
{
    [DataMember(Name = "id", Order = 0)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [DataMember(Name = "name", Order = 1)] public string Name { get; set; } = "";
    [DataMember(Name = "hotkey", Order = 2, EmitDefaultValue = false)] private string? HotkeyText { get; set; }
    [DataMember(Name = "mode", Order = 3, EmitDefaultValue = false)] private string? ModeText { get; set; }
    [DataMember(Name = "repeat", Order = 4, EmitDefaultValue = false)] private int RepeatValue { get; set; }
    [DataMember(Name = "enabled", Order = 5)] public bool Enabled { get; set; } = true;
    [DataMember(Name = "pad", Order = 6, EmitDefaultValue = false)] public bool OnPad { get; set; }
    [DataMember(Name = "created", Order = 7, EmitDefaultValue = false)] public DateTime Created { get; set; } = DateTime.UtcNow;
    [DataMember(Name = "steps", Order = 8)] private List<Step>? StepList { get; set; } = new List<Step>();

    public Hotkey Hotkey
    {
        get => Hotkey.Parse(HotkeyText);
        set => HotkeyText = value.IsEmpty ? null : value.Serialize();
    }

    public PlayMode Mode
    {
        get => Enum.TryParse<PlayMode>(ModeText, true, out var mode) ? mode : PlayMode.Once;
        set => ModeText = value == PlayMode.Once ? null : value.ToString();
    }

    public int RepeatCount
    {
        get => RepeatValue < 1 ? 2 : RepeatValue;
        set => RepeatValue = Math.Max(1, Math.Min(100000, value));
    }

    public List<Step> Steps
    {
        get => StepList ??= new List<Step>();
        set => StepList = value;
    }

    public Macro Clone()
    {
        var copy = (Macro)MemberwiseClone();
        copy.StepList = new List<Step>();
        foreach (var step in Steps) copy.StepList.Add(step.Clone());
        return copy;
    }
}

/// <summary>One action. A flat record so the file stays readable and tolerant: each kind uses a few fields.</summary>
[DataContract]
public sealed class Step
{
    [DataMember(Name = "kind", Order = 0)] private string KindText { get; set; } = "Delay";

    // Key
    [DataMember(Name = "key", Order = 1, EmitDefaultValue = false)] public int Key { get; set; }
    [DataMember(Name = "scan", Order = 2, EmitDefaultValue = false)] public int Scan { get; set; }
    [DataMember(Name = "ext", Order = 3, EmitDefaultValue = false)] public bool Extended { get; set; }
    [DataMember(Name = "action", Order = 4, EmitDefaultValue = false)] private string? ActionText { get; set; }

    // Mouse
    [DataMember(Name = "button", Order = 5, EmitDefaultValue = false)] private string? ButtonText { get; set; }
    [DataMember(Name = "x", Order = 6, EmitDefaultValue = false)] public int X { get; set; }
    [DataMember(Name = "y", Order = 7, EmitDefaultValue = false)] public int Y { get; set; }
    [DataMember(Name = "amount", Order = 8, EmitDefaultValue = false)] public int Amount { get; set; }
    [DataMember(Name = "horizontal", Order = 9, EmitDefaultValue = false)] public bool Horizontal { get; set; }

    // Path: delta-encoded samples, see PathCodec
    [DataMember(Name = "path", Order = 10, EmitDefaultValue = false)] public string? PathData { get; set; }
    [DataMember(Name = "relative", Order = 11, EmitDefaultValue = false)] public bool Relative { get; set; }

    // Text
    [DataMember(Name = "text", Order = 12, EmitDefaultValue = false)] public string? Text { get; set; }
    [DataMember(Name = "charDelay", Order = 13, EmitDefaultValue = false)] public int CharDelay { get; set; }

    // Delay
    [DataMember(Name = "ms", Order = 14, EmitDefaultValue = false)] public int Ms { get; set; }
    [DataMember(Name = "msMax", Order = 15, EmitDefaultValue = false)] public int MsMax { get; set; }

    // Loop
    [DataMember(Name = "count", Order = 16, EmitDefaultValue = false)] public int Count { get; set; }

    // Command / Open
    [DataMember(Name = "command", Order = 17, EmitDefaultValue = false)] public string? Command { get; set; }
    [DataMember(Name = "shell", Order = 18, EmitDefaultValue = false)] private string? ShellText { get; set; }
    [DataMember(Name = "wait", Order = 19, EmitDefaultValue = false)] public bool Wait { get; set; }
    [DataMember(Name = "hidden", Order = 20, EmitDefaultValue = false)] public bool Hidden { get; set; }
    [DataMember(Name = "target", Order = 21, EmitDefaultValue = false)] public string? Target { get; set; }
    [DataMember(Name = "args", Order = 22, EmitDefaultValue = false)] public string? Arguments { get; set; }

    [DataMember(Name = "note", Order = 23, EmitDefaultValue = false)] public string? Note { get; set; }

    /// <summary>Modifiers held around a key press built in the editor (Ctrl + S as one step).</summary>
    [DataMember(Name = "mods", Order = 24, EmitDefaultValue = false)] private int ModsValue { get; set; }

    /// <summary>
    /// How long a press or click built in the editor holds before releasing, in ms. 0 (the default) sends down and up
    /// in one SendInput call, as AutoHotkey does; some games need 10–30 ms to notice a press.
    /// </summary>
    [DataMember(Name = "hold", Order = 27, EmitDefaultValue = false)] private int HoldValue { get; set; }

    public int HoldMs
    {
        get => Math.Max(0, Math.Min(10000, HoldValue));
        set => HoldValue = Math.Max(0, Math.Min(10000, value));
    }

    /// <summary>PowerShell source of a script step; its param() block defines the fields the editor shows.</summary>
    [DataMember(Name = "script", Order = 25, EmitDefaultValue = false)] public string? Script { get; set; }

    [DataMember(Name = "scriptArgs", Order = 26, EmitDefaultValue = false)] private List<ScriptArg>? ArgList { get; set; }

    /// <summary>Values set in the editor, by parameter name; parameters without one use the script's default.</summary>
    public Dictionary<string, string> ScriptValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ArgList != null) foreach (var arg in ArgList) if (arg != null && !string.IsNullOrEmpty(arg.Name)) values[arg.Name] = arg.Value ?? "";
        return values;
    }

    /// <summary>Back to the script's own default.</summary>
    public void ClearScriptValue(string name) => ArgList?.RemoveAll(a => a == null || string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public void SetScriptValue(string name, string value)
    {
        ArgList ??= new List<ScriptArg>();
        var existing = ArgList.Find(a => a != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.Value = value;
        else ArgList.Add(new ScriptArg { Name = name, Value = value });
    }

    // Image block: the picture to look for (PNG, base64), how alike it must be, and how the search runs.
    [DataMember(Name = "image", Order = 28, EmitDefaultValue = false)] public string? ImageData { get; set; }
    [DataMember(Name = "threshold", Order = 29, EmitDefaultValue = false)] private int ThresholdValue { get; set; }
    [DataMember(Name = "clickImage", Order = 30, EmitDefaultValue = false)] public bool ClickImage { get; set; }
    [DataMember(Name = "interval", Order = 31, EmitDefaultValue = false)] private int IntervalValue { get; set; }
    [DataMember(Name = "timeout", Order = 32, EmitDefaultValue = false)] private int TimeoutValue { get; set; }

    public const int DefaultThreshold = 90, DefaultIntervalMs = 250, MinIntervalMs = 10;

    /// <summary>How alike the screen must be to the picture, 1–100 % (unset reads as 90 %).</summary>
    public int Threshold
    {
        get => ThresholdValue <= 0 ? DefaultThreshold : Math.Min(100, ThresholdValue);
        set => ThresholdValue = Math.Max(1, Math.Min(100, value));
    }

    /// <summary>Wait between two searches while the picture is not on screen; at least 10 ms, so a search never spins a core.</summary>
    public int IntervalMs
    {
        get => IntervalValue <= 0 ? DefaultIntervalMs : Math.Max(MinIntervalMs, Math.Min(86_400_000, IntervalValue));
        set => IntervalValue = Math.Max(MinIntervalMs, Math.Min(86_400_000, value));
    }

    /// <summary>Gives up and skips the block after this long without a match; 0 keeps searching until the macro stops.</summary>
    public int TimeoutMs
    {
        get => Math.Max(0, Math.Min(86_400_000, TimeoutValue));
        set => TimeoutValue = Math.Max(0, Math.Min(86_400_000, value));
    }

    public Modifiers Modifiers
    {
        get => (Modifiers)(ModsValue & 0xF);
        set => ModsValue = (int)value & 0xF;
    }

    public StepKind Kind
    {
        get => Enum.TryParse<StepKind>(KindText, true, out var kind) ? kind : StepKind.Delay;
        set => KindText = value.ToString();
    }

    public KeyAction KeyAction
    {
        get => Enum.TryParse<KeyAction>(ActionText, true, out var a) ? a : KeyAction.Press;
        set => ActionText = value.ToString();
    }

    public MouseAction MouseAction
    {
        get => Enum.TryParse<MouseAction>(ActionText, true, out var a) ? a : MouseAction.Click;
        set => ActionText = value.ToString();
    }

    public MouseButton Button
    {
        get => Enum.TryParse<MouseButton>(ButtonText, true, out var b) ? b : MouseButton.Left;
        set => ButtonText = value == MouseButton.Left ? null : value.ToString();
    }

    public ShellKind Shell
    {
        get => Enum.TryParse<ShellKind>(ShellText, true, out var s) ? s : ShellKind.Cmd;
        set => ShellText = value == ShellKind.Cmd ? null : value.ToString();
    }

    /// <summary>A deep copy: undo snapshots and duplicates must never share script values.</summary>
    public Step Clone()
    {
        var copy = (Step)MemberwiseClone();
        if (ArgList != null) copy.ArgList = ArgList.ConvertAll(a => new ScriptArg { Name = a?.Name ?? "", Value = a?.Value ?? "" });
        return copy;
    }

    public static Step KeyStep(int vk, int scan, bool extended, KeyAction action) =>
        new Step { Kind = StepKind.Key, Key = vk, Scan = scan, Extended = extended, KeyAction = action };

    public static Step MouseStep(MouseAction action, MouseButton button = MouseButton.Left) =>
        new Step { Kind = StepKind.Mouse, MouseAction = action, Button = button };

    /// <summary>Pause after each round of a loop (stored in <c>ms</c> on the loop start); never below 1 ms, so a loop can't flood input.</summary>
    public int LoopPauseMs
    {
        get => Math.Max(1, Ms);
        set => Ms = Math.Max(1, Math.Min(86_400_000, value));
    }

    public static Step DelayStep(int ms) => new Step { Kind = StepKind.Delay, Ms = Math.Max(0, ms) };
}

[DataContract]
public sealed class LibraryFile
{
    [DataMember(Name = "format", Order = 0)] public int Format { get; set; } = 1;
    [DataMember(Name = "macros", Order = 1)] public List<Macro>? Macros { get; set; } = new List<Macro>();
}

/// <summary>Virtual-key codes used by name in code.</summary>
public static class Vk
{
    public const int LButton = 0x01, RButton = 0x02, Cancel = 0x03, MButton = 0x04, XButton1 = 0x05, XButton2 = 0x06;
    public const int Back = 0x08, Tab = 0x09, Return = 0x0D, Shift = 0x10, Control = 0x11, Menu = 0x12, Pause = 0x13, Capital = 0x14, Escape = 0x1B, Space = 0x20;
    public const int Prior = 0x21, Next = 0x22, End = 0x23, Home = 0x24, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
    public const int Snapshot = 0x2C, Insert = 0x2D, Delete = 0x2E, LWin = 0x5B, RWin = 0x5C, Apps = 0x5D;
    public const int NumLock = 0x90, Scroll = 0x91, LShift = 0xA0, RShift = 0xA1, LControl = 0xA2, RControl = 0xA3, LMenu = 0xA4, RMenu = 0xA5;
    public const int Divide = 0x6F, F1 = 0x70, F24 = 0x87;

    public static bool IsModifier(int vk) =>
        vk == Shift || vk == Control || vk == Menu || vk == LShift || vk == RShift || vk == LControl || vk == RControl ||
        vk == LMenu || vk == RMenu || vk == LWin || vk == RWin;

    public static Modifiers ModifierOf(int vk)
    {
        switch (vk)
        {
            case Shift: case LShift: case RShift: return Modifiers.Shift;
            case Control: case LControl: case RControl: return Modifiers.Ctrl;
            case Menu: case LMenu: case RMenu: return Modifiers.Alt;
            case LWin: case RWin: return Modifiers.Win;
            default: return Modifiers.None;
        }
    }

    /// <summary>Keys whose scan code needs the extended flag (arrows, navigation block, right-hand modifiers, numpad divide).</summary>
    public static bool IsExtended(int vk) =>
        (vk >= Prior && vk <= Down) || vk == Insert || vk == Delete || vk == LWin || vk == RWin || vk == Apps ||
        vk == RControl || vk == RMenu || vk == Divide || vk == NumLock || vk == Snapshot ||
        (vk >= 0xA6 && vk <= 0xB7); // browser and media keys
}
