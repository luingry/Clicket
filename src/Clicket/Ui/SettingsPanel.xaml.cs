using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Clicket.Core;
using Clicket.Shell;

namespace Clicket.Ui;

/// <summary>Settings as an overlay on the main window; every change applies and saves at once.</summary>
public partial class SettingsPanel : UserControl
{
    private static readonly string[] LanguageValues = { AppSettings.LanguageAuto, Strings.English, Strings.PortugueseBrazil };
    private bool _loading;

    public SettingsPanel()
    {
        InitializeComponent();
        // The two global hotkeys must differ: the stop hotkey is matched first, so a shared one would never record.
        StopHotkeyBox.Captured += hotkey => SetGlobalHotkey(hotkey, Host.Settings.RecordHotkey, StopHotkeyBox, s => s.StopHotkey, (s, h) => s.StopHotkey = h);
        RecordHotkeyBox.Captured += hotkey => SetGlobalHotkey(hotkey, Host.Settings.StopHotkey, RecordHotkeyBox, s => s.RecordHotkey, (s, h) => s.RecordHotkey = h);
        DelaysSelector.SelectionChanged += index =>
        {
            Update(s => s.RecordDelays = (DelayMode)index);
            FixedDelayPanel.Visibility = index == (int)DelayMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
        };
        Strings.LanguageChanged += (s, e) => Dispatcher.BeginInvoke(new Action(FillLanguages));
        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape && !(Keyboard.FocusedElement is KeyCaptureBox)) Hide();
        };
    }

    private static App Host => (App)Application.Current;

    public void ShowPanel(bool animate = true)
    {
        Load();
        Visibility = Visibility.Visible;
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            var duration = Artwork.Token<Duration>("Motion.Slow");
            var ease = Artwork.Token<IEasingFunction>("Motion.Emphasized");
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Artwork.Token<Duration>("Motion.Normal")));
            CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        }
        Focus();
    }

    public void Hide()
    {
        if (Visibility != Visibility.Visible) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new DoubleAnimation(0, Artwork.Token<Duration>("Motion.Fast"));
        fade.Completed += (s, e) =>
        {
            BeginAnimation(OpacityProperty, null);
            Visibility = Visibility.Collapsed;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var s = Host.Settings;
            FillLanguages();
            var hosted = Host.IsHosted;
            StartupSwitch.Visibility = hosted ? Visibility.Collapsed : Visibility.Visible;
            HideSwitch.Visibility = hosted ? Visibility.Collapsed : Visibility.Visible;
            HostedNote.Visibility = hosted ? Visibility.Visible : Visibility.Collapsed;
            if (!hosted)
            {
                StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
                HideSwitch.IsChecked = s.HideOnClose;
            }
            StopHotkeyBox.Value = s.StopHotkey;
            RecordHotkeyBox.Value = s.RecordHotkey;
            ButtonsSwitch.IsChecked = s.RecordButtons;
            MovementSwitch.IsChecked = s.RecordMovement;
            DelaysSelector.SelectedIndex = (int)s.RecordDelays;
            FixedDelayPanel.Visibility = s.RecordDelays == DelayMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
            FixedDelayBox.Text = s.FixedDelayMs.ToString(CultureInfo.InvariantCulture);
            AppIcon.Source = Artwork.AppIconImage();
            VersionText.Text = Strings.Format("Version", App.Version);
        }
        finally
        {
            _loading = false;
        }
    }

    private void FillLanguages()
    {
        var wasLoading = _loading;
        _loading = true;
        LanguageBox.ItemsSource = new[] { Strings.Get("LanguageAuto"), "English", "Português (Brasil)" };
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageValues, Host.Settings.Language));
        _loading = wasLoading;
    }

    private void Update(Action<AppSettings> change, bool hotkeys = false)
    {
        if (_loading) return;
        change(Host.Settings);
        Host.SaveSettings();
        if (hotkeys) Host.ApplyEngineConfiguration();
    }

    private void SetGlobalHotkey(Hotkey hotkey, Hotkey other, KeyCaptureBox box, Func<AppSettings, Hotkey> read, Action<AppSettings, Hotkey> write)
    {
        var clash = !hotkey.IsEmpty && hotkey == other;
        HotkeyError.Visibility = clash ? Visibility.Visible : Visibility.Collapsed;
        if (clash)
        {
            box.Value = read(Host.Settings);
            return;
        }
        Update(s => write(s, hotkey), hotkeys: true);
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0) return;
        var value = LanguageValues[LanguageBox.SelectedIndex];
        Update(s => s.Language = value);
        Host.ApplyLanguage();
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            StartupRegistration.Set(StartupSwitch.IsChecked == true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
        {
            Log.Warn("Run key not writable: " + ex.GetType().Name);
        }
        StartupSwitch.IsChecked = StartupRegistration.IsEnabled;
    }

    private void OnHideChanged(object sender, RoutedEventArgs e) => Update(s => s.HideOnClose = HideSwitch.IsChecked == true);

    private void OnRecordingChanged(object sender, RoutedEventArgs e) => Update(s =>
    {
        s.RecordButtons = ButtonsSwitch.IsChecked == true;
        s.RecordMovement = MovementSwitch.IsChecked == true;
    });

    private void OnFixedDelayChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(FixedDelayBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)) Update(s => s.FixedDelayMs = ms);
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Host.DataFolder);
            using var process = Process.Start(new ProcessStartInfo(Host.DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is System.IO.IOException)
        {
            Log.Warn("Could not open the data folder: " + ex.GetType().Name);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => Hide();
}
