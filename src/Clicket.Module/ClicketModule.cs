using System.IO;
using System.Windows;
using SwissTechKnife.Sdk;

namespace Clicket.Module;

/// <summary>
/// SwissTechKnife entry point. Clicket keeps its own process and window; the module starts it with the host
/// (so hotkeys work at once), restarts it after a crash, stops it when disabled and opens its window.
/// </summary>
public sealed class ClicketModule : IBackgroundModule, IModuleWindow
{
    /// <summary>Folder (inside the package) holding the app build; see scripts/pack-module.ps1.</summary>
    internal const string AppFolder = "app";
    internal const string AppExecutable = "Clicket.Hosted.exe";

    private IModuleContext? _context;
    private HostedAppSupervisor? _supervisor;

    public void Initialize(IModuleContext context)
    {
        _context = context;
        _supervisor = new HostedAppSupervisor(Path.Combine(context.InstallDirectory, AppFolder, AppExecutable), context.DataDirectory, context.Log);
    }

    public void Start() => _supervisor?.Start();

    public void Stop() => _supervisor?.Stop();

    public FrameworkElement CreateView() => new ModulePage(_supervisor!, _context!);

    /// <summary>Double click on the module's notification-area icon opens the Clicket window, like the page's button.</summary>
    public void ShowWindow() => _supervisor?.ShowWindow();

    public void Dispose()
    {
        _supervisor?.Dispose();
        _supervisor = null;
    }
}
