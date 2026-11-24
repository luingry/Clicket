using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Clicket.Core;

/// <summary>Human names for virtual keys and hotkeys. Punctuation keys follow the active keyboard layout.</summary>
public static class KeyNames
{
    private static readonly Dictionary<int, string> Fixed = new Dictionary<int, string>
    {
        [Vk.LButton] = "Mouse L", [Vk.RButton] = "Mouse R", [Vk.MButton] = "Mouse M", [Vk.XButton1] = "Mouse X1", [Vk.XButton2] = "Mouse X2",
        [Vk.Back] = "Backspace", [Vk.Tab] = "Tab", [0x0C] = "Clear", [Vk.Return] = "Enter", [Vk.Shift] = "Shift", [Vk.Control] = "Ctrl", [Vk.Menu] = "Alt",
        [Vk.Pause] = "Pause", [Vk.Capital] = "Caps Lock", [Vk.Escape] = "Esc", [Vk.Space] = "Space",
        [Vk.Prior] = "Page Up", [Vk.Next] = "Page Down", [Vk.End] = "End", [Vk.Home] = "Home",
        [Vk.Left] = "←", [Vk.Up] = "↑", [Vk.Right] = "→", [Vk.Down] = "↓",
        [Vk.Snapshot] = "Print Screen", [Vk.Insert] = "Insert", [Vk.Delete] = "Delete", [Vk.LWin] = "Win", [Vk.RWin] = "Win (R)", [Vk.Apps] = "Menu",
        [0x5F] = "Sleep", [0x6A] = "Num *", [0x6B] = "Num +", [0x6C] = "Num Enter", [0x6D] = "Num -", [0x6E] = "Num .", [Vk.Divide] = "Num /",
        [Vk.NumLock] = "Num Lock", [Vk.Scroll] = "Scroll Lock",
        [Vk.LShift] = "Shift", [Vk.RShift] = "Shift (R)", [Vk.LControl] = "Ctrl", [Vk.RControl] = "Ctrl (R)", [Vk.LMenu] = "Alt", [Vk.RMenu] = "AltGr",
        [0xA6] = "Browser Back", [0xA7] = "Browser Forward", [0xA8] = "Browser Refresh", [0xA9] = "Browser Stop", [0xAA] = "Browser Search",
        [0xAB] = "Browser Favorites", [0xAC] = "Browser Home", [0xAD] = "Volume Mute", [0xAE] = "Volume Down", [0xAF] = "Volume Up",
        [0xB0] = "Next Track", [0xB1] = "Previous Track", [0xB2] = "Stop Media", [0xB3] = "Play/Pause", [0xB4] = "Mail", [0xB5] = "Media Select",
        [0xB6] = "App 1", [0xB7] = "App 2",
    };

    public static string Name(int vk)
    {
        if (Fixed.TryGetValue(vk, out var name)) return name;
        if ((vk >= '0' && vk <= '9') || (vk >= 'A' && vk <= 'Z')) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60).ToString(CultureInfo.InvariantCulture);
        if (vk >= Vk.F1 && vk <= Vk.F24) return "F" + (vk - Vk.F1 + 1).ToString(CultureInfo.InvariantCulture);
        var layout = LayoutCharacter(vk);
        return layout ?? "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    public static string Describe(Hotkey hotkey)
    {
        if (hotkey.IsEmpty) return "";
        var text = new StringBuilder();
        if ((hotkey.Modifiers & Modifiers.Ctrl) != 0) text.Append("Ctrl + ");
        if ((hotkey.Modifiers & Modifiers.Alt) != 0) text.Append("Alt + ");
        if ((hotkey.Modifiers & Modifiers.Shift) != 0) text.Append("Shift + ");
        if ((hotkey.Modifiers & Modifiers.Win) != 0) text.Append("Win + ");
        text.Append(Name(hotkey.Key));
        return text.ToString();
    }

    private static string? LayoutCharacter(int vk)
    {
        try
        {
            var mapped = MapVirtualKey((uint)vk, 2) & 0x7FFF; // MAPVK_VK_TO_CHAR; the high bit marks a dead key
            if (mapped == 0 || char.IsControl((char)mapped)) return null;
            return char.ToUpper((char)mapped, CultureInfo.CurrentCulture).ToString();
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
}
