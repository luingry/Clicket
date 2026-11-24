using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Clicket.Core;

/// <summary>User preferences, stored as a small key=value file. Unknown keys and bad values are ignored.</summary>
public sealed class AppSettings
{
    public const string LanguageAuto = "auto";

    public static readonly Hotkey DefaultStopHotkey = new Hotkey(Vk.End, Modifiers.Ctrl | Modifiers.Alt);
    public static readonly Hotkey DefaultRecordHotkey = new Hotkey('R', Modifiers.Ctrl | Modifiers.Alt);

    private int _fixedDelay = 50;

    /// <summary>"auto" follows SwissTechKnife when it is installed, else English; "en" or "pt-BR" pins it.</summary>
    public string Language { get; set; } = LanguageAuto;
    public bool HideOnClose { get; set; } = true;
    public Hotkey StopHotkey { get; set; } = DefaultStopHotkey;
    public Hotkey RecordHotkey { get; set; } = DefaultRecordHotkey;
    public bool RecordButtons { get; set; } = true;
    public bool RecordMovement { get; set; } = true;
    public DelayMode RecordDelays { get; set; } = DelayMode.Recorded;
    public bool PadVisible { get; set; }
    public int PadX { get; set; } = int.MinValue;
    public int PadY { get; set; } = int.MinValue;
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public int FixedDelayMs
    {
        get => _fixedDelay;
        set => _fixedDelay = Math.Max(0, Math.Min(60000, value));
    }

    public RecordingOptions ToRecordingOptions() => new RecordingOptions
    {
        Keyboard = true,
        Buttons = RecordButtons,
        Movement = RecordMovement,
        Delays = RecordDelays,
        FixedDelayMs = FixedDelayMs,
    };

    public static AppSettings Parse(string text)
    {
        var s = new AppSettings();
        foreach (var line in text.Split('\r', '\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();
            switch (key)
            {
                case "language": s.Language = NormalizeLanguageSetting(value); break;
                case "hideOnClose": if (TryBool(value, out var hide)) s.HideOnClose = hide; break;
                case "stopHotkey": s.StopHotkey = value.Length == 0 ? default : Hotkey.Parse(value); break;
                case "recordHotkey": s.RecordHotkey = value.Length == 0 ? default : Hotkey.Parse(value); break;
                case "recordButtons": if (TryBool(value, out var buttons)) s.RecordButtons = buttons; break;
                case "recordMovement": if (TryBool(value, out var movement)) s.RecordMovement = movement; break;
                case "recordDelays": if (Enum.TryParse<DelayMode>(value, true, out var delays)) s.RecordDelays = delays; break;
                case "fixedDelay": if (TryInt(value, out var fixedDelay)) s.FixedDelayMs = fixedDelay; break;
                case "padVisible": if (TryBool(value, out var pad)) s.PadVisible = pad; break;
                case "padX": if (TryInt(value, out var padX)) s.PadX = padX; break;
                case "padY": if (TryInt(value, out var padY)) s.PadY = padY; break;
                case "windowX": if (TryInt(value, out var wx)) s.WindowX = wx; break;
                case "windowY": if (TryInt(value, out var wy)) s.WindowY = wy; break;
                case "windowWidth": if (TryInt(value, out var ww)) s.WindowWidth = Math.Max(0, ww); break;
                case "windowHeight": if (TryInt(value, out var wh)) s.WindowHeight = Math.Max(0, wh); break;
                case "windowMaximized": if (TryBool(value, out var max)) s.WindowMaximized = max; break;
            }
        }
        return s;
    }

    public string Serialize()
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            Pair("language", Language),
            Pair("hideOnClose", Bool(HideOnClose)),
            Pair("stopHotkey", StopHotkey.Serialize()),
            Pair("recordHotkey", RecordHotkey.Serialize()),
            Pair("recordButtons", Bool(RecordButtons)),
            Pair("recordMovement", Bool(RecordMovement)),
            Pair("recordDelays", RecordDelays.ToString()),
            Pair("fixedDelay", Int(FixedDelayMs)),
            Pair("padVisible", Bool(PadVisible)),
            Pair("padX", Int(PadX)),
            Pair("padY", Int(PadY)),
            Pair("windowX", Int(WindowX)),
            Pair("windowY", Int(WindowY)),
            Pair("windowWidth", Int(WindowWidth)),
            Pair("windowHeight", Int(WindowHeight)),
            Pair("windowMaximized", Bool(WindowMaximized)),
        };
        var text = new StringBuilder();
        foreach (var pair in pairs) text.Append(pair.Key).Append('=').Append(pair.Value).Append(Environment.NewLine);
        return text.ToString();
    }

    public static AppSettings Load(string path) => AtomicFile.ReadAllText(path) is string text ? Parse(text) : new AppSettings();

    public void Save(string path) => AtomicFile.WriteAllText(path, Serialize());

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public static string NormalizeLanguageSetting(string? value)
    {
        if (string.Equals(value, Strings.English, StringComparison.OrdinalIgnoreCase)) return Strings.English;
        if (string.Equals(value, Strings.PortugueseBrazil, StringComparison.OrdinalIgnoreCase)) return Strings.PortugueseBrazil;
        return LanguageAuto;
    }

    private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static bool TryInt(string value, out int result) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryBool(string value, out bool result)
    {
        result = value == "true" || value == "1";
        return result || value == "false" || value == "0";
    }
}
