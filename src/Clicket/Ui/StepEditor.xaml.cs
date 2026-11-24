using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clicket.Core;

namespace Clicket.Ui;

/// <summary>
/// Edits the selected step in place: only the fields of its kind are shown, and every change applies at once.
/// <see cref="Changing"/> fires before a change (undo snapshot), <see cref="Changed"/> after it.
/// </summary>
public partial class StepEditor : UserControl
{
    private static readonly MouseAction[] MouseActions = { MouseAction.Click, MouseAction.DoubleClick, MouseAction.Down, MouseAction.Up, MouseAction.MoveTo, MouseAction.MoveBy, MouseAction.Scroll };
    private static readonly MouseButton[] Buttons = { MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.X1, MouseButton.X2 };
    private StepItem? _item;
    private bool _loading;

    public StepEditor()
    {
        InitializeComponent();
        KeyBox.Captured += hotkey => Apply(s =>
        {
            s.Key = hotkey.Key;
            s.Modifiers = hotkey.Modifiers;
            s.Scan = 0;
            s.Extended = Vk.IsExtended(hotkey.Key);
        });
        KeyActionSelector.SelectionChanged += index => Apply(s => s.KeyAction = (KeyAction)index);
        ShellSelector.SelectionChanged += index => Apply(s => s.Shell = (ShellKind)index);
        Strings.LanguageChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => Load(_item)));
    }

    /// <summary>Before a field changes the step (undo snapshot), with a key that groups typing in one field.</summary>
    public event Action<string>? Changing;

    public event Action<StepItem>? Changed;

    public StepItem? Item => _item;

    public void Load(StepItem? item)
    {
        _loading = true;
        try
        {
            _item = item;
            EmptyText.Visibility = item == null ? Visibility.Visible : Visibility.Collapsed;
            Body.Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
            if (item == null) return;
            var step = item.Step;
            GlyphChip.Background = item.HueSoft;
            GlyphText.Foreground = item.Hue;
            GlyphText.Text = item.Glyph;
            StepNumber.Text = Strings.Format("StepHeader", item.Index + 1).ToUpper(Strings.Culture);
            KindTitle.Text = Strings.Get(step.Kind == StepKind.Path ? "CatPath" : "Cat" + StepFormatter.CategoryKey(step.Kind));

            Show(KeyPanel, step.Kind == StepKind.Key);
            Show(MousePanel, step.Kind == StepKind.Mouse);
            Show(PathPanel, step.Kind == StepKind.Path);
            Show(TextPanel, step.Kind == StepKind.Text);
            Show(DelayPanel, step.Kind == StepKind.Delay);
            Show(LoopPanel, step.Kind == StepKind.LoopStart);
            Show(LoopEndPanel, step.Kind == StepKind.LoopEnd);
            Show(CommandPanel, step.Kind == StepKind.Command);
            Show(OpenPanel, step.Kind == StepKind.Open);
            Show(ScriptPanel, step.Kind == StepKind.Script);

            switch (step.Kind)
            {
                case StepKind.Key:
                    KeyBox.Value = step.Key == 0 ? default : new Hotkey(step.Key, step.KeyAction == KeyAction.Press ? step.Modifiers : Modifiers.None);
                    KeyActionSelector.SelectedIndex = (int)step.KeyAction;
                    break;
                case StepKind.Mouse:
                    MouseActionBox.ItemsSource = Array.ConvertAll(MouseActions, a => Strings.Get("Mouse" + a));
                    MouseActionBox.SelectedIndex = Array.IndexOf(MouseActions, step.MouseAction);
                    ButtonBox.ItemsSource = Array.ConvertAll(Buttons, StepFormatter.ButtonName);
                    ButtonBox.SelectedIndex = Array.IndexOf(Buttons, step.Button);
                    XBox.Text = step.X.ToString(CultureInfo.InvariantCulture);
                    YBox.Text = step.Y.ToString(CultureInfo.InvariantCulture);
                    NotchesBox.Text = (step.Amount / 120.0).ToString("0.##", CultureInfo.InvariantCulture);
                    HorizontalBox.IsChecked = step.Horizontal;
                    UpdateMouseRows();
                    break;
                case StepKind.Path:
                    var info = PathCodec.Measure(step.PathData);
                    PathInfo.Text = Strings.Format("PathInfo", info.Count.ToString("N0", Strings.Culture), Strings.Duration(info.Micros / 1000));
                    RelativeSwitch.IsChecked = step.Relative;
                    break;
                case StepKind.Text:
                    TextField.Text = step.Text ?? "";
                    CharDelayBox.Text = step.CharDelay.ToString(CultureInfo.InvariantCulture);
                    break;
                case StepKind.Delay:
                    DelayBox.Text = step.Ms.ToString(CultureInfo.InvariantCulture);
                    DelayMaxBox.Text = step.MsMax.ToString(CultureInfo.InvariantCulture);
                    break;
                case StepKind.LoopStart:
                    LoopCountBox.Text = step.Count.ToString(CultureInfo.InvariantCulture);
                    break;
                case StepKind.Command:
                    CommandBox.Text = step.Command ?? "";
                    ShellSelector.SelectedIndex = (int)step.Shell;
                    WaitSwitch.IsChecked = step.Wait;
                    HiddenSwitch.IsChecked = step.Hidden;
                    break;
                case StepKind.Script:
                    ScriptBox.Text = step.Script ?? "";
                    ScriptWaitSwitch.IsChecked = step.Wait;
                    ScriptHiddenSwitch.IsChecked = step.Hidden;
                    _scriptSignature = null;
                    BuildScriptFields();
                    break;
                case StepKind.Open:
                    TargetBox.Text = step.Target ?? "";
                    ArgumentsBox.Text = step.Arguments ?? "";
                    break;
            }
            NoteBox.Text = step.Note ?? "";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Puts the keyboard in the field a freshly inserted step needs first.</summary>
    public void FocusPrimaryField()
    {
        if (_item == null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (_item?.Step.Kind)
            {
                case StepKind.Key: KeyBox.Focus(); break;
                case StepKind.Text: TextField.Focus(); break;
                case StepKind.Delay: DelayBox.Focus(); DelayBox.SelectAll(); break;
                case StepKind.LoopStart: LoopCountBox.Focus(); LoopCountBox.SelectAll(); break;
                case StepKind.Command: CommandBox.Focus(); break;
                case StepKind.Open: TargetBox.Focus(); break;
                case StepKind.Script: ScriptBox.Focus(); break;
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void Apply(Action<Step> change, string group = "")
    {
        if (_loading || _item == null) return;
        Changing?.Invoke(group);
        change(_item.Step);
        _item.Refresh();
        Changed?.Invoke(_item);
    }

    private static bool TryInt(TextBox box, int min, int max, out int value)
    {
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;
        value = Math.Max(min, Math.Min(max, value));
        return true;
    }

    private void UpdateMouseRows()
    {
        var action = MouseActionBox.SelectedIndex >= 0 ? MouseActions[MouseActionBox.SelectedIndex] : MouseAction.Click;
        Show(ButtonRow, action <= MouseAction.Up);
        Show(PositionRow, action == MouseAction.MoveTo || action == MouseAction.MoveBy);
        Show(PickButton, action == MouseAction.MoveTo);
        Show(ScrollRow, action == MouseAction.Scroll);
        PositionLabel.Text = Strings.Get(action == MouseAction.MoveBy ? "Offset" : "Position");
        NotchesLabel.Text = Strings.Get(HorizontalBox.IsChecked == true ? "NotchesHorizontal" : "Notches");
    }

    private void OnMouseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MouseActionBox.SelectedIndex < 0) return;
        var action = MouseActions[MouseActionBox.SelectedIndex];
        Apply(s =>
        {
            s.MouseAction = action;
            if (action == MouseAction.Scroll && s.Amount == 0) s.Amount = 120;
        });
        if (!_loading) Load(_item);
    }

    private void OnButtonChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ButtonBox.SelectedIndex >= 0) Apply(s => s.Button = Buttons[ButtonBox.SelectedIndex]);
    }

    private void OnPositionChanged(object sender, TextChangedEventArgs e)
    {
        if (!TryInt(XBox, -100000, 100000, out var x) || !TryInt(YBox, -100000, 100000, out var y)) return;
        Apply(s =>
        {
            s.X = x;
            s.Y = y;
        }, "position");
    }

    private void OnNotchesChanged(object sender, TextChangedEventArgs e)
    {
        if (!double.TryParse(NotchesBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var notches)) return;
        notches = Math.Max(-100, Math.Min(100, notches));
        Apply(s => s.Amount = (int)Math.Round(notches * 120), "notches");
    }

    private void OnHorizontalChanged(object sender, RoutedEventArgs e)
    {
        Apply(s => s.Horizontal = HorizontalBox.IsChecked == true);
        UpdateMouseRows();
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        PositionPicker.Pick(owner, point =>
        {
            Apply(s =>
            {
                s.X = point.X;
                s.Y = point.Y;
            });
            Load(_item);
        });
    }

    private void OnRelativeChanged(object sender, RoutedEventArgs e) => Apply(s => s.Relative = RelativeSwitch.IsChecked == true);

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Apply(s => s.Text = TextField.Text, "text");

    private void OnCharDelayChanged(object sender, TextChangedEventArgs e)
    {
        if (TryInt(CharDelayBox, 0, 10000, out var ms)) Apply(s => s.CharDelay = ms, "chardelay");
    }

    private void OnDelayChanged(object sender, TextChangedEventArgs e)
    {
        if (!TryInt(DelayBox, 0, 86_400_000, out var ms)) return;
        if (!TryInt(DelayMaxBox, 0, 86_400_000, out var max)) max = 0;
        Apply(s =>
        {
            s.Ms = ms;
            s.MsMax = max > ms ? max : 0;
        }, "delay");
    }

    private void OnLoopCountChanged(object sender, TextChangedEventArgs e)
    {
        if (TryInt(LoopCountBox, 0, 1_000_000, out var count)) Apply(s => s.Count = count, "loop");
    }

    private void OnCommandChanged(object sender, TextChangedEventArgs e) => Apply(s => s.Command = CommandBox.Text, "command");

    private void OnCommandFlagsChanged(object sender, RoutedEventArgs e) => Apply(s =>
    {
        s.Wait = WaitSwitch.IsChecked == true;
        s.Hidden = HiddenSwitch.IsChecked == true;
    });

    private void OnOpenChanged(object sender, TextChangedEventArgs e) => Apply(s =>
    {
        s.Target = TargetBox.Text;
        s.Arguments = ArgumentsBox.Text.Length == 0 ? null : ArgumentsBox.Text;
    }, "open");

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) TargetBox.Text = dialog.FileName;
    }

    // ---------- Script ----------

    private string? _scriptSignature;
    private System.Windows.Threading.DispatcherTimer? _scriptTimer;

    private void OnScriptChanged(object sender, TextChangedEventArgs e)
    {
        Apply(s => s.Script = ScriptBox.Text, "script");
        if (_loading) return;
        // Re-read param() shortly after typing pauses; fields rebuild only when the declaration really changed.
        if (_scriptTimer == null)
        {
            _scriptTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _scriptTimer.Tick += (s, args) =>
            {
                _scriptTimer!.Stop();
                BuildScriptFields();
            };
        }
        _scriptTimer.Stop();
        _scriptTimer.Start();
    }

    private void OnScriptFlagsChanged(object sender, RoutedEventArgs e) => Apply(s =>
    {
        s.Wait = ScriptWaitSwitch.IsChecked == true;
        s.Hidden = ScriptHiddenSwitch.IsChecked == true;
    });

    /// <summary>One field per declared parameter, like the GUI an AutoHotkey script builds: text, number, switch or list.</summary>
    private void BuildScriptFields()
    {
        if (_item == null || _item.Step.Kind != StepKind.Script) return;
        var step = _item.Step;
        var parameters = ScriptParams.Parse(step.Script);
        var signature = string.Join("\n", parameters.ConvertAll(p => p.Name + "|" + p.Type + "|" + p.Default + "|" + p.Label + "|" + string.Join(",", p.Choices)));
        if (signature == _scriptSignature) return;
        _scriptSignature = signature;

        ScriptFields.Children.Clear();
        ScriptParamsHint.Text = Strings.Get(parameters.Count == 0 ? "ScriptNoParams" : "ScriptParamsHint");
        var values = step.ScriptValues();
        foreach (var parameter in parameters)
        {
            var p = parameter;
            var value = values.TryGetValue(p.Name, out var v) ? v : p.Default;
            var label = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label!;

            if (p.Type == ScriptParamType.Toggle)
            {
                var toggle = new Switch { IsChecked = ScriptParams.IsTrue(value), Margin = new Thickness(0, 14, 0, 0) };
                toggle.SetResourceReference(StyleProperty, "Switch");
                var text = new TextBlock { Text = label };
                text.SetResourceReference(StyleProperty, "Text.Body");
                toggle.Content = text;
                toggle.Click += (s, e) => Apply(st => st.SetScriptValue(p.Name, toggle.IsChecked == true ? "true" : "false"));
                ScriptFields.Children.Add(toggle);
                continue;
            }

            var caption = new TextBlock { Text = label };
            caption.SetResourceReference(StyleProperty, "Field.Label");
            ScriptFields.Children.Add(caption);

            if (p.Type == ScriptParamType.Choice)
            {
                var list = new ComboBox { ItemsSource = p.Choices, SelectedItem = p.Choices.Contains(value) ? value : p.Default };
                list.SelectionChanged += (s, e) =>
                {
                    if (list.SelectedItem is string chosen) Apply(st => st.SetScriptValue(p.Name, chosen));
                };
                ScriptFields.Children.Add(list);
                continue;
            }

            // Untouched fields show the script's default as a placeholder; clearing a field returns to that default.
            var box = new TextBox { Text = values.TryGetValue(p.Name, out var set) ? set : "" };
            UiProps.SetPlaceholder(box, p.Default.Length > 0 ? p.Default : p.Name);
            box.TextChanged += (s, e) =>
            {
                var input = box.Text.Trim();
                if (box.Text.Length == 0)
                {
                    Apply(st => st.ClearScriptValue(p.Name), "arg:" + p.Name);
                    return;
                }
                var valid = p.Type == ScriptParamType.Integer ? long.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    : p.Type == ScriptParamType.Number ? double.TryParse(input.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    : true;
                if (valid) Apply(st => st.SetScriptValue(p.Name, p.Type == ScriptParamType.Number ? input.Replace(',', '.') : box.Text), "arg:" + p.Name);
            };
            ScriptFields.Children.Add(box);
        }
    }

    private void OnNoteChanged(object sender, TextChangedEventArgs e) => Apply(s => s.Note = NoteBox.Text.Trim().Length == 0 ? null : NoteBox.Text, "note");

    internal static Brush Token(string key) => Artwork.Token<Brush>(key);
}
