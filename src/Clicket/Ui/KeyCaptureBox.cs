using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clicket.Core;

namespace Clicket.Ui;

/// <summary>
/// A field that captures a key combination when focused: Ctrl + Alt + F1, a mouse side button for hotkeys,
/// or a lone modifier for key steps. While any capture field has focus, global hotkeys pause so they can be captured too.
/// </summary>
public sealed class KeyCaptureBox : TextBox
{
    public static readonly DependencyProperty AllowMouseProperty = DependencyProperty.Register(nameof(AllowMouse), typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false));
    public static readonly DependencyProperty AllowModifierOnlyProperty = DependencyProperty.Register(nameof(AllowModifierOnly), typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false));

    private Hotkey _value;
    private bool _pendingModifier;
    private int _lastModifier;

    public KeyCaptureBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        IsUndoEnabled = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    /// <summary>Raised while any capture field gains (true) or loses (false) focus.</summary>
    public static event Action<bool>? ListeningChanged;

    public event Action<Hotkey>? Captured;

    public bool AllowMouse
    {
        get => (bool)GetValue(AllowMouseProperty);
        set => SetValue(AllowMouseProperty, value);
    }

    public bool AllowModifierOnly
    {
        get => (bool)GetValue(AllowModifierOnlyProperty);
        set => SetValue(AllowModifierOnlyProperty, value);
    }

    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value;
            ShowValue();
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        ListeningChanged?.Invoke(true);
        Text = "";
        UiProps.SetPlaceholder(this, Strings.Get("HotkeyListening"));
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ListeningChanged?.Invoke(false);
        _pendingModifier = false;
        ShowValue();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        var modifiers = CurrentModifiers();
        if (Vk.IsModifier(vk))
        {
            _pendingModifier = true;
            _lastModifier = vk;
            Text = Describe(modifiers) + "…";
            return;
        }
        _pendingModifier = false;
        if (modifiers == Modifiers.None && vk == Vk.Escape)
        {
            Leave();
            return;
        }
        Commit(new Hotkey(vk, modifiers));
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (key == Key.Snapshot)
        {
            Commit(new Hotkey(Vk.Snapshot, CurrentModifiers())); // Windows delivers Print Screen as a key up only
            return;
        }
        if (_pendingModifier && Vk.IsModifier(vk) && AllowModifierOnly)
        {
            // A modifier pressed and released alone is the key itself (a step that holds Shift, say).
            _pendingModifier = false;
            Commit(new Hotkey(_lastModifier, Modifiers.None));
            return;
        }
        if (_pendingModifier && CurrentModifiers() == Modifiers.None)
        {
            _pendingModifier = false;
            Text = "";
        }
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (IsKeyboardFocused && AllowMouse && (e.ChangedButton == System.Windows.Input.MouseButton.Middle || e.ChangedButton == System.Windows.Input.MouseButton.XButton1 || e.ChangedButton == System.Windows.Input.MouseButton.XButton2))
        {
            e.Handled = true;
            var vk = e.ChangedButton == System.Windows.Input.MouseButton.Middle ? Vk.MButton : e.ChangedButton == System.Windows.Input.MouseButton.XButton1 ? Vk.XButton1 : Vk.XButton2;
            Commit(new Hotkey(vk, CurrentModifiers()));
            return;
        }
        base.OnPreviewMouseDown(e);
    }

    protected override void OnTextInput(TextCompositionEventArgs e) => e.Handled = true;

    private void Commit(Hotkey hotkey)
    {
        _value = hotkey;
        Captured?.Invoke(hotkey);
        Leave();
    }

    private void Leave()
    {
        // Focus moves to the window so the field shows its value and hotkeys resume.
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
        ShowValue();
    }

    private void ShowValue()
    {
        UiProps.SetPlaceholder(this, Strings.Get(AllowModifierOnly ? "KeyPlaceholder" : "HotkeyPlaceholder"));
        Text = _value.IsEmpty ? "" : _value.ToString();
    }

    private static Modifiers CurrentModifiers()
    {
        var m = Modifiers.None;
        var k = Keyboard.Modifiers;
        if ((k & ModifierKeys.Control) != 0) m |= Modifiers.Ctrl;
        if ((k & ModifierKeys.Alt) != 0) m |= Modifiers.Alt;
        if ((k & ModifierKeys.Shift) != 0) m |= Modifiers.Shift;
        if ((k & ModifierKeys.Windows) != 0) m |= Modifiers.Win;
        return m;
    }

    private static string Describe(Modifiers modifiers)
    {
        var text = "";
        if ((modifiers & Modifiers.Ctrl) != 0) text += "Ctrl + ";
        if ((modifiers & Modifiers.Alt) != 0) text += "Alt + ";
        if ((modifiers & Modifiers.Shift) != 0) text += "Shift + ";
        if ((modifiers & Modifiers.Win) != 0) text += "Win + ";
        return text;
    }
}
