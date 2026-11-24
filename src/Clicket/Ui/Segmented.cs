using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Clicket.Core;

namespace Clicket.Ui;

/// <summary>
/// 2–4 exclusive choices with one shared indicator that travels to the chosen item (never teleports).
/// Labels come from localization keys in <see cref="Keys"/> and follow a language change.
/// </summary>
public sealed class Segmented : Border
{
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(Segmented),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((Segmented)d).OnSelectedIndexChanged()));

    private readonly Grid _grid = new Grid();
    private readonly StackPanel _items = new StackPanel { Orientation = Orientation.Horizontal };
    private readonly Border _indicator = new Border { HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly TranslateTransform _travel = new TranslateTransform();
    private readonly List<RadioButton> _buttons = new List<RadioButton>();
    private readonly string _group = "seg" + Guid.NewGuid().ToString("N");
    private string _keys = "";
    private string[] _tips = new string[0];
    private bool _syncing;

    public Segmented()
    {
        SetResourceReference(StyleProperty, "Segmented.Container");
        _indicator.SetResourceReference(BackgroundProperty, "Brush.Surface5");
        _indicator.SetResourceReference(CornerRadiusProperty, "Radius.Sm");
        _indicator.RenderTransform = _travel;
        _grid.Children.Add(_indicator);
        _grid.Children.Add(_items);
        Child = _grid;
        Loaded += (s, e) => Place(animate: false);
        Strings.LanguageChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Relabel));
        _items.SizeChanged += (s, e) => Place(animate: false);
    }

    public event Action<int>? SelectionChanged;

    /// <summary>Comma-separated localization keys, one per item.</summary>
    public string Keys
    {
        get => _keys;
        set
        {
            _keys = value;
            Build();
        }
    }

    /// <summary>Optional comma-separated tooltip keys.</summary>
    public string TipKeys
    {
        get => string.Join(",", _tips);
        set
        {
            _tips = value.Split(',');
            Relabel();
        }
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private void Build()
    {
        _items.Children.Clear();
        _buttons.Clear();
        foreach (var key in _keys.Split(','))
        {
            var button = new RadioButton { GroupName = _group, Tag = key.Trim() };
            button.SetResourceReference(StyleProperty, "Segmented.Item");
            var index = _buttons.Count;
            button.Checked += (s, e) =>
            {
                if (_syncing) return;
                SelectedIndex = index;
                SelectionChanged?.Invoke(index);
            };
            _buttons.Add(button);
            _items.Children.Add(button);
        }
        Relabel();
        OnSelectedIndexChanged();
    }

    private void Relabel()
    {
        for (var i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].Content = Strings.Get((string)_buttons[i].Tag);
            _buttons[i].ToolTip = i < _tips.Length && _tips[i].Trim().Length > 0 ? Strings.Get(_tips[i].Trim()) : null;
        }
        Dispatcher.BeginInvoke(new Action(() => Place(animate: false)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnSelectedIndexChanged()
    {
        var index = SelectedIndex;
        if (index < 0 || index >= _buttons.Count) return;
        _syncing = true;
        _buttons[index].IsChecked = true;
        _syncing = false;
        Place(animate: IsLoaded);
    }

    private void Place(bool animate)
    {
        var index = SelectedIndex;
        if (index < 0 || index >= _buttons.Count) return;
        var target = _buttons[index];
        if (target.ActualWidth <= 0) return;
        var x = target.TranslatePoint(new Point(0, 0), _items).X;
        _indicator.Height = target.ActualHeight;
        _indicator.VerticalAlignment = VerticalAlignment.Center;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _travel.BeginAnimation(TranslateTransform.XProperty, null);
            _indicator.BeginAnimation(WidthProperty, null);
            _travel.X = x;
            _indicator.Width = target.ActualWidth;
            return;
        }
        var duration = Artwork.Token<Duration>("Motion.Normal");
        var ease = Artwork.Token<IEasingFunction>("Motion.Emphasized");
        _travel.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, duration) { EasingFunction = ease });
        _indicator.BeginAnimation(WidthProperty, new DoubleAnimation(target.ActualWidth, duration) { EasingFunction = ease });
    }
}
