using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Clicket.Core;

/// <summary>
/// Command line of the copy that the SwissTechKnife module runs (<c>--hosted --parent &lt;pid&gt; --data &lt;dir&gt; [--show]</c>).
/// The host then owns tray, auto-start and updates, and the data lives in the module's folder.
/// </summary>
public sealed class HostedOptions
{
    public const string HostedArg = "--hosted", ParentArg = "--parent", DataArg = "--data", ShowArg = "--show";

    /// <summary>Exit code when a standalone Clicket already owns the single-instance mutex.</summary>
    public const int AlreadyRunningExitCode = 3;

    public const string ExitEventName = @"Local\Clicket.Hosted.Exit";
    public const string ActivateEventName = @"Local\Clicket.App.Activate";

    private HostedOptions(int parent, string data, bool show)
    {
        ParentId = parent;
        DataDirectory = data;
        Show = show;
    }

    public int ParentId { get; }
    public string DataDirectory { get; }
    public bool Show { get; }

    public static HostedOptions? TryParse(string[] args)
    {
        var hosted = false;
        var show = false;
        var parent = 0;
        string? data = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, HostedArg, StringComparison.OrdinalIgnoreCase)) hosted = true;
            else if (string.Equals(arg, ShowArg, StringComparison.OrdinalIgnoreCase)) show = true;
            else if (string.Equals(arg, ParentArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) int.TryParse(args[++i], out parent);
            else if (string.Equals(arg, DataArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) data = args[++i];
        }
        if (!hosted || string.IsNullOrWhiteSpace(data) || !Path.IsPathRooted(data)) return null;
        return new HostedOptions(parent, data!, show);
    }
}

/// <summary>Resolves the UI language: an explicit choice wins, "auto" follows SwissTechKnife, and English is the default.</summary>
public static class LanguageResolver
{
    private static readonly Regex LanguageField = new Regex("\"language\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant);

    public static string Resolve(string setting, string? stkSettingsPath)
    {
        var normalized = AppSettings.NormalizeLanguageSetting(setting);
        if (normalized != AppSettings.LanguageAuto) return normalized;
        return ReadStkLanguage(stkSettingsPath) ?? Strings.English;
    }

    /// <summary>The language SwissTechKnife stores in its settings.json, or null when absent or unreadable.</summary>
    public static string? ReadStkLanguage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            var match = LanguageField.Match(File.ReadAllText(path));
            return match.Success ? Strings.Normalize(match.Groups[1].Value) : Strings.English;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>%LOCALAPPDATA%\SwissTechKnife\settings.json, or STK_HOME when the host runs isolated.</summary>
    public static string StkSettingsPath()
    {
        var home = Environment.GetEnvironmentVariable("STK_HOME");
        var root = !string.IsNullOrWhiteSpace(home) && Path.IsPathRooted(home) ? home! : Path.Combine(UserFolders.Local, "SwissTechKnife");
        return Path.Combine(root, "settings.json");
    }
}
