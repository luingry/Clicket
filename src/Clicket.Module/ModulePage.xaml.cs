using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Animation;
using SwissTechKnife.Sdk;

namespace Clicket.Module;

/// <summary>
/// The module page: whether Clicket is running, and the way into its own window. Every option lives in that window,
/// so nothing here duplicates it. Text follows SwissTechKnife's language.
/// </summary>
public partial class ModulePage
{
    private readonly HostedAppSupervisor _supervisor;
    private readonly IModuleContext _context;
    private readonly bool _pt;
    private HostedAppState? _shown;

    internal ModulePage(HostedAppSupervisor supervisor, IModuleContext context)
    {
        _supervisor = supervisor;
        _context = context;
        _pt = HostLanguageIsPortuguese();
        InitializeComponent();
        Heading.Text = T("Macros", "Macros");
        About.Text = T(
            "Recording, editing and the macro list live in the Clicket window. Clicket runs in its own process, so nothing in SwissTechKnife can delay a macro, and its hotkeys work while this window is closed.",
            "Gravação, edição e a lista de macros ficam na janela do Clicket. Ele roda em um processo próprio, então nada no SwissTechKnife atrasa uma macro, e os atalhos funcionam com esta janela fechada.");
        OpenButton.Content = T("Open Clicket", "Abrir o Clicket");
        _supervisor.StateChanged += OnStateChanged;
        Apply(_supervisor.State);
    }

    private string T(string en, string pt) => _pt ? pt : en;

    private void OnStateChanged(HostedAppState state) => _context.Dispatcher.BeginInvoke(new Action(() => Apply(_supervisor.State)));

    private void Apply(HostedAppState state)
    {
        if (_shown == state) return;
        var first = _shown == null;
        _shown = state;

        string label, detail, soft, strong;
        switch (state)
        {
            case HostedAppState.Running:
                label = T("Running", "Em execução");
                detail = T("Your macro hotkeys work in every app.", "Os atalhos das suas macros funcionam em qualquer aplicativo.");
                soft = "Brush.Success.Soft";
                strong = "Brush.Success";
                break;
            case HostedAppState.Starting:
                label = T("Starting", "Iniciando");
                detail = T("Clicket is starting.", "O Clicket está iniciando.");
                soft = "Brush.Info.Soft";
                strong = "Brush.Info";
                break;
            case HostedAppState.RunningOutside:
                label = T("Open outside SwissTechKnife", "Aberto fora do SwissTechKnife");
                detail = T("Another Clicket is already open and keeps running your hotkeys. The module takes over when it closes.",
                    "Outro Clicket já está aberto e continua cuidando dos atalhos. O módulo assume quando ele for fechado.");
                soft = "Brush.Warning.Soft";
                strong = "Brush.Warning";
                break;
            case HostedAppState.Failed:
                label = T("Stopped", "Parado");
                detail = T("Clicket closed unexpectedly several times. Open it to try again.", "O Clicket fechou inesperadamente várias vezes. Abra-o para tentar de novo.");
                soft = "Brush.Danger.Soft";
                strong = "Brush.Danger";
                break;
            default:
                label = T("Stopped", "Parado");
                detail = T("Clicket is not running. Open it to turn your hotkeys back on.", "O Clicket não está em execução. Abra-o para reativar os atalhos.");
                soft = "Brush.Surface4";
                strong = "Brush.Text.Secondary";
                break;
        }

        StatusText.Text = label;
        StatusDetail.Text = detail;
        StatusChip.SetResourceReference(BackgroundProperty, soft);
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, strong);
        if (!first) FadeIn(StatusChip, StatusDetail);
    }

    private void FadeIn(params UIElement[] elements)
    {
        var duration = TryFindResource("Motion.Normal") is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(200));
        foreach (var element in elements)
        {
            var fade = new DoubleAnimation(0.4, 1, duration) { EasingFunction = TryFindResource("Ease.Out") as IEasingFunction };
            fade.Freeze();
            element.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e) => _supervisor.ShowWindow();

    /// <summary>The host keeps its language in settings.json ("language": "pt-BR"); English when absent.</summary>
    private static bool HostLanguageIsPortuguese()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("STK_HOME");
            var root = !string.IsNullOrWhiteSpace(home) && Path.IsPathRooted(home)
                ? home!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "SwissTechKnife");
            var file = Path.Combine(root, "settings.json");
            if (!File.Exists(file)) return false;
            var match = Regex.Match(File.ReadAllText(file), "\"language\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success && string.Equals(match.Groups[1].Value, "pt-BR", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return false;
        }
    }
}
