using System.Windows;

namespace Clicket.Ui;

/// <summary>Attached properties the theme templates read: a leading glyph, a placeholder and a hover tint.</summary>
public static class UiProps
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(UiProps), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(UiProps), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.RegisterAttached("Accent", typeof(System.Windows.Media.Brush), typeof(UiProps), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);
    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);
    public static System.Windows.Media.Brush? GetAccent(DependencyObject d) => (System.Windows.Media.Brush?)d.GetValue(AccentProperty);
    public static void SetAccent(DependencyObject d, System.Windows.Media.Brush? value) => d.SetValue(AccentProperty, value);
}
