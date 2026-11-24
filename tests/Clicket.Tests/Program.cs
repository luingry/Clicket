using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Clicket.Core;

namespace Clicket.Tests;

internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        var folder = Path.Combine(Path.GetTempPath(), "clicket-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            AtomicWriteKeepsTheOldVersion(Path.Combine(folder, "nested", "file.txt"));
            ReadFallsBackToTheBackup(Path.Combine(folder, "fallback.txt"));
            SettingsRoundTrip(Path.Combine(folder, "settings.ini"));
            SettingsIgnoreJunk();
            FoldersAreNeverEmpty();
            PathCodecIsLossless();
            PathCodecRejectsDamage();
            RecorderCollapsesRepeatsAndDropsStrays();
            RecorderKeepsMotionAsOnePath();
            RecorderWithoutMotionPositionsClicks();
            RecorderDelayModes();
            RecorderStopHotkeyAfterMotion();
            ProgramPairsLoops();
            EstimateCountsLoopsAndPaths();
            LibraryRoundTripAndQuarantine(folder);
            ImportGetsAFreshId();
            HotkeySerialization();
            StringsHaveBothLanguages();
            HostedArguments();
            LanguageFollowsStk(folder);
            FormatterDescribesSteps();
            WaiterKeepsTheRecordedClock();
            ScriptParametersBecomeFields();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
        Console.WriteLine(_failures == 0 ? "OK" : _failures + " failure(s)");
        return _failures == 0 ? 0 : 1;
    }

    private static void AtomicWriteKeepsTheOldVersion(string path)
    {
        AtomicFile.WriteAllText(path, "primeira versão");
        Check(File.ReadAllText(path) == "primeira versão", "writes UTF-8 text");
        Check(File.ReadAllBytes(path)[0] != 0xEF, "writes no BOM");
        AtomicFile.WriteAllText(path, "second");
        Check(File.ReadAllText(path + ".bak") == "primeira versão", "keeps the old content as .bak");
        Check(!File.Exists(path + ".tmp"), "leaves no temp file");
    }

    private static void ReadFallsBackToTheBackup(string path)
    {
        Check(AtomicFile.ReadAllText(path) == null, "nothing to read is null");
        AtomicFile.WriteAllText(path, "good");
        AtomicFile.WriteAllText(path, "newer");
        File.WriteAllText(path, "");
        Check(AtomicFile.ReadAllText(path) == "good", "an empty file falls back to .bak");
    }

    private static void SettingsRoundTrip(string path)
    {
        var s = new AppSettings
        {
            Language = Strings.PortugueseBrazil,
            HideOnClose = false,
            StopHotkey = new Hotkey(Vk.Pause, Modifiers.None),
            RecordHotkey = default,
            RecordMovement = false,
            RecordDelays = DelayMode.Fixed,
            FixedDelayMs = 75,
            PadVisible = true,
            PadX = -300,
        };
        s.Save(path);
        var loaded = AppSettings.Load(path);
        Check(loaded.Language == Strings.PortugueseBrazil, "language round-trips");
        Check(!loaded.HideOnClose, "hide on close round-trips");
        Check(loaded.StopHotkey == new Hotkey(Vk.Pause, Modifiers.None), "stop hotkey round-trips");
        Check(loaded.RecordHotkey.IsEmpty, "a cleared hotkey stays cleared");
        Check(!loaded.RecordMovement && loaded.RecordDelays == DelayMode.Fixed && loaded.FixedDelayMs == 75, "recording options round-trip");
        Check(loaded.PadVisible && loaded.PadX == -300, "pad state round-trips, negative coordinates included");
    }

    private static void SettingsIgnoreJunk()
    {
        var s = AppSettings.Parse("junk\nlanguage=klingon\nfixedDelay=999999\nhideOnClose=maybe\n=3");
        Check(s.Language == AppSettings.LanguageAuto, "unknown language becomes auto");
        Check(s.FixedDelayMs == 60000, "fixed delay clamps");
        Check(s.HideOnClose, "bad booleans keep the default");
        Check(new AppSettings().StopHotkey == AppSettings.DefaultStopHotkey, "default stop hotkey");
        Check(new AppSettings().RecordHotkey.IsEmpty, "recording has no hotkey by default");
        Check(AppSettings.Parse("recordHotkey=3:82").RecordHotkey.IsEmpty, "1.0.0's saved Ctrl+Alt+R default is cleared once");
        Check(AppSettings.Parse("recordHotkey=3:83").RecordHotkey == new Hotkey('S', Modifiers.Ctrl | Modifiers.Alt), "a hotkey the user picked in 1.0.0 is kept");
        var picked = new AppSettings { RecordHotkey = new Hotkey('R', Modifiers.Ctrl | Modifiers.Alt) };
        Check(AppSettings.Parse(picked.Serialize()).RecordHotkey == picked.RecordHotkey, "Ctrl+Alt+R chosen after the migration stays");
    }

    private static void FoldersAreNeverEmpty()
    {
        const Environment.SpecialFolder Folder = Environment.SpecialFolder.ApplicationData;
        Check(UserFolders.Resolve(Folder, "V", "rel", f => @"C:\special", v => null) == @"C:\special", "uses the special folder");
        Check(UserFolders.Resolve(Folder, "V", "rel", f => "", v => v == "V" ? @"C:\env" : null) == @"C:\env", "falls back to the variable");
        Check(UserFolders.Resolve(Folder, "V", "rel", f => "", v => v == "USERPROFILE" ? @"C:\home" : null) == @"C:\home\rel", "falls back to the profile");
    }

    private static void PathCodecIsLossless()
    {
        var random = new Random(7);
        var points = new List<PathPoint>();
        int x = 1920, y = -400;
        long t = 0;
        for (var i = 0; i < 5000; i++)
        {
            x += random.Next(-40, 41);
            y += random.Next(-40, 41);
            t += random.Next(100, 8000);
            points.Add(new PathPoint(x, y, t));
        }
        var encoded = PathCodec.Encode(points);
        var decoded = PathCodec.Decode(encoded);
        Check(decoded.Count == points.Count, "path keeps every sample");
        Check(decoded.Zip(points, (a, b) => a.X == b.X && a.Y == b.Y && a.Micros == b.Micros).All(same => same), "path samples are exact, negative coordinates included");
        Check(encoded.Length < points.Count * 8, "path encoding is compact (" + encoded.Length + " chars for " + points.Count + " samples)");
        var measure = PathCodec.Measure(encoded);
        Check(measure.Count == points.Count && measure.Micros == t, "measure reports count and duration");
    }

    private static void PathCodecRejectsDamage()
    {
        Check(PathCodec.Decode("not base64!").Count == 0, "garbage decodes to nothing");
        var encoded = PathCodec.Encode(new[] { new PathPoint(1, 2, 3), new PathPoint(4, 5, 6) });
        var truncated = Convert.ToBase64String(Convert.FromBase64String(encoded).Take(4).ToArray());
        Check(PathCodec.Decode(truncated).Count == 0, "a truncated path decodes to nothing, never half");
        Check(PathCodec.Decode(null).Count == 0 && PathCodec.Measure("").Count == 0, "empty path is empty");
    }

    private static RawEvent Key(RawKind kind, int vk, long ms) => new RawEvent { Kind = kind, Vk = vk, Micros = ms * 1000 };

    private static RawEvent Mouse(RawKind kind, int x, int y, long micros, MouseButton button = MouseButton.Left) =>
        new RawEvent { Kind = kind, X = x, Y = y, Micros = micros, Button = button };

    private static void RecorderCollapsesRepeatsAndDropsStrays()
    {
        var builder = new RecordingBuilder(new RecordingOptions { Movement = false });
        builder.Add(Key(RawKind.KeyUp, Vk.LControl, 0));      // held before the start
        builder.Add(Key(RawKind.KeyDown, 'A', 100));
        builder.Add(Key(RawKind.KeyDown, 'A', 130));           // auto-repeat
        builder.Add(Key(RawKind.KeyDown, 'A', 160));
        builder.Add(Key(RawKind.KeyUp, 'A', 250));
        builder.Add(Key(RawKind.KeyDown, Vk.LControl, 900));  // stop hotkey modifiers
        builder.Add(Key(RawKind.KeyDown, Vk.LMenu, 920));
        var steps = builder.Finish(dropPendingPath: false);
        Check(steps.Count == 3, "down, delay, up survive (got " + steps.Count + ")");
        Check(steps[0].Kind == StepKind.Key && steps[0].KeyAction == KeyAction.Down && steps[0].Key == 'A', "first is A down");
        Check(steps[1].Kind == StepKind.Delay && steps[1].Ms == 150, "held for 150 ms");
        Check(steps[2].KeyAction == KeyAction.Up, "then A up; dangling modifiers dropped");
    }

    private static void RecorderKeepsMotionAsOnePath()
    {
        var builder = new RecordingBuilder(new RecordingOptions { Movement = true });
        builder.Add(Key(RawKind.KeyDown, 'Q', 0));
        builder.Add(Key(RawKind.KeyUp, 'Q', 10));
        for (var i = 1; i <= 100; i++) builder.Add(Mouse(RawKind.Move, 100 + i, 200 + i, 10_000 + i * 1000));
        builder.Add(Mouse(RawKind.Move, 200, 300, 110_000)); // same point twice: dropped
        builder.Add(Mouse(RawKind.ButtonDown, 200, 300, 150_000));
        builder.Add(Mouse(RawKind.ButtonUp, 200, 300, 230_000));
        for (var i = 1; i <= 10; i++) builder.Add(Mouse(RawKind.Move, 200 - i, 300, 230_000 + i * 2000)); // trip to the stop button
        var steps = builder.Finish(dropPendingPath: true);
        var path = steps.Single(s => s.Kind == StepKind.Path);
        var samples = PathCodec.Decode(path.PathData);
        Check(samples.Count == 100, "every distinct move sample kept (got " + samples.Count + ")");
        Check(samples[0].Micros == 1000 && samples[99].Micros == 100_000, "path time counts from the previous event");
        var afterPath = steps[steps.IndexOf(path) + 1];
        Check(afterPath.Kind == StepKind.Delay && afterPath.Ms == 40, "the pause after the motion is a delay (got " + afterPath.Ms + ")");
        Check(steps.Count(s => s.Kind == StepKind.Path) == 1, "the trip to the stop button is dropped");
        Check(steps.Last().Kind == StepKind.Mouse && steps.Last().MouseAction == MouseAction.Up, "ends with the button up");
    }

    private static void RecorderWithoutMotionPositionsClicks()
    {
        var builder = new RecordingBuilder(new RecordingOptions { Movement = false, Delays = DelayMode.None });
        builder.Add(Mouse(RawKind.Move, 5, 5, 0));
        builder.Add(Mouse(RawKind.ButtonDown, 50, 60, 1000, MouseButton.Right));
        builder.Add(Mouse(RawKind.ButtonUp, 50, 60, 2000, MouseButton.Right));
        var steps = builder.Finish(false);
        Check(steps.Count == 3, "move, down, up (got " + steps.Count + ")");
        Check(steps[0].MouseAction == MouseAction.MoveTo && steps[0].X == 50 && steps[0].Y == 60, "click lands where it happened");
        Check(steps[1].Button == MouseButton.Right && steps[1].MouseAction == MouseAction.Down, "right button down");
        Check(steps.All(s => s.Kind != StepKind.Delay), "no delays when delays are off");
    }

    private static void RecorderStopHotkeyAfterMotion()
    {
        var builder = new RecordingBuilder(new RecordingOptions { Movement = true });
        builder.Add(Mouse(RawKind.ButtonDown, 300, 400, 0)); // a click before the pointer ever moved
        builder.Add(Mouse(RawKind.ButtonUp, 300, 400, 50_000));
        builder.Add(Key(RawKind.KeyDown, Vk.LControl, 500));  // stop hotkey: Ctrl, Alt, then a little jitter
        builder.Add(Key(RawKind.KeyDown, Vk.LMenu, 520));
        builder.Add(Mouse(RawKind.Move, 301, 400, 530_000));
        builder.Add(Mouse(RawKind.ButtonDown, 301, 400, 540_000)); // a drag released over Clicket: no up recorded
        var steps = builder.Finish(dropPendingPath: false);
        Check(steps[0].Kind == StepKind.Mouse && steps[0].MouseAction == MouseAction.MoveTo && steps[0].X == 300, "a click before any motion is positioned");
        Check(!steps.Any(s => s.Kind == StepKind.Key), "stop-hotkey modifiers are dropped even after motion");
        Check(steps.Count(s => s.Kind == StepKind.Mouse && s.MouseAction == MouseAction.Down) == 1, "an unreleased button down is dropped");
        Check(steps.Last().Kind != StepKind.Delay, "no trailing delay");
    }

    private static void RecorderDelayModes()
    {
        var builder = new RecordingBuilder(new RecordingOptions { Delays = DelayMode.Fixed, FixedDelayMs = 30 });
        builder.Add(Key(RawKind.KeyDown, 'A', 0));
        builder.Add(Key(RawKind.KeyUp, 'A', 999));
        var steps = builder.Finish(false);
        Check(steps.Count == 3 && steps[1].Ms == 30, "fixed delay replaces the recorded one");

        var carry = new RecordingBuilder(new RecordingOptions());
        carry.Add(new RawEvent { Kind = RawKind.KeyDown, Vk = 'A', Micros = 0 });
        carry.Add(new RawEvent { Kind = RawKind.KeyUp, Vk = 'A', Micros = 1500 });
        carry.Add(new RawEvent { Kind = RawKind.KeyDown, Vk = 'B', Micros = 3000 });
        carry.Add(new RawEvent { Kind = RawKind.KeyUp, Vk = 'B', Micros = 4000 });
        var delays = carry.Finish(false).Where(s => s.Kind == StepKind.Delay).Sum(s => s.Ms);
        Check(delays == 4, "sub-millisecond remainders carry over instead of drifting (got " + delays + ")");
    }

    private static void ProgramPairsLoops()
    {
        var steps = new List<Step>
        {
            new Step { Kind = StepKind.LoopStart, Count = 2 },
            new Step { Kind = StepKind.LoopStart, Count = 3 },
            Step.DelayStep(1),
            new Step { Kind = StepKind.LoopEnd },
            new Step { Kind = StepKind.LoopEnd },
        };
        var program = MacroProgram.Build(steps, out var error);
        Check(error == -1, "balanced loops are valid");
        Check(program.Partner[0] == 4 && program.Partner[1] == 3 && program.Partner[3] == 1, "loops pair innermost first");
        Check(program.Depth[2] == 2 && program.Depth[3] == 1 && program.Depth[4] == 0, "depth drives indentation");
        MacroProgram.Build(new List<Step> { new Step { Kind = StepKind.LoopEnd } }, out error);
        Check(error == 0, "a stray end is reported");
        MacroProgram.Build(new List<Step> { Step.DelayStep(1), new Step { Kind = StepKind.LoopStart } }, out error);
        Check(error == 1, "an unclosed start is reported");
    }

    private static void EstimateCountsLoopsAndPaths()
    {
        var steps = new List<Step>
        {
            new Step { Kind = StepKind.LoopStart, Count = 3 },
            Step.DelayStep(100),
            new Step { Kind = StepKind.LoopEnd },
            new Step { Kind = StepKind.Path, PathData = PathCodec.Encode(new[] { new PathPoint(0, 0, 1000), new PathPoint(1, 1, 250_000) }) },
            new Step { Kind = StepKind.Delay, Ms = 100, MsMax = 300 },
        };
        Check(MacroProgram.EstimateMillis(steps) == 300 + 2 + 250 + 200, "estimate multiplies loops, adds their pauses and paths, and averages random delays");
        Check(new Step { Kind = StepKind.LoopStart }.LoopPauseMs == 1 && new Step { Kind = StepKind.LoopStart, LoopPauseMs = 0 }.LoopPauseMs == 1, "a loop pauses at least 1 ms between rounds");
        steps[0].Count = 0;
        Check(MacroProgram.EstimateMillis(steps) == null, "an endless loop has no estimate");
    }

    private static void LibraryRoundTripAndQuarantine(string folder)
    {
        var path = Path.Combine(folder, "macros.json");
        var macro = new Macro { Name = "Teste ção", Hotkey = new Hotkey(Vk.F1, Modifiers.Ctrl), Mode = PlayMode.Repeat, RepeatCount = 5, OnPad = true };
        macro.Steps.Add(Step.KeyStep('A', 30, false, KeyAction.Press));
        macro.Steps[0].Modifiers = Modifiers.Shift;
        macro.Steps[0].HoldMs = 25;
        macro.Steps.Add(new Step { Kind = StepKind.Text, Text = "olá\n🙂" });
        macro.Steps.Add(new Step { Kind = StepKind.Mouse, MouseAction = MouseAction.Scroll, Amount = -240, Horizontal = true });
        macro.Steps.Add(new Step { Kind = StepKind.Command, Command = "echo 1", Shell = ShellKind.PowerShell, Wait = true });
        MacroStore.Save(path, new[] { macro });
        var loaded = MacroStore.Load(path).Single();
        Check(loaded.Id == macro.Id && loaded.Name == "Teste ção", "macro identity and unicode name round-trip");
        Check(loaded.Hotkey == macro.Hotkey && loaded.Mode == PlayMode.Repeat && loaded.RepeatCount == 5 && loaded.OnPad, "trigger settings round-trip");
        Check(loaded.Steps[0].Modifiers == Modifiers.Shift && loaded.Steps[0].Scan == 30 && loaded.Steps[0].HoldMs == 25, "key step round-trips, hold time included");
        Check(loaded.Steps[1].HoldMs == 0, "presses and clicks are instant unless a hold time is set");
        Check(loaded.Steps[1].Text == "olá\n🙂", "text keeps newlines and emoji");
        Check(loaded.Steps[2].MouseAction == MouseAction.Scroll && loaded.Steps[2].Amount == -240 && loaded.Steps[2].Horizontal, "scroll round-trips");
        Check(loaded.Steps[3].Shell == ShellKind.PowerShell && loaded.Steps[3].Wait, "command round-trips");

        MacroStore.Save(path, new[] { macro, new Macro { Name = "second" } });
        File.WriteAllText(path, "{ broken");
        var recovered = MacroStore.Load(path);
        Check(recovered.Count == 1 && recovered[0].Name == "Teste ção", "a corrupt file falls back to the backup");
        Check(Directory.GetFiles(folder, "macros.json.corrupt-*").Length == 1, "the corrupt file is quarantined, not deleted");
    }

    private static void ImportGetsAFreshId()
    {
        var macro = new Macro { Name = "exported" };
        var imported = MacroStore.ImportMacro(MacroStore.ExportMacro(macro));
        Check(imported != null && imported.Name == "exported" && imported.Id != macro.Id, "import keeps content with a new id");
        Check(MacroStore.ImportMacro("[1,2,3]") == null && MacroStore.ImportMacro("nonsense") == null, "non-macro files are refused");
    }

    private static void HotkeySerialization()
    {
        var hotkey = new Hotkey(Vk.XButton2, Modifiers.Ctrl | Modifiers.Win);
        Check(Hotkey.Parse(hotkey.Serialize()) == hotkey, "hotkey round-trips");
        Check(hotkey.IsMouse, "side button is a mouse hotkey");
        Check(Hotkey.Parse("x:y").IsEmpty && Hotkey.Parse("0:999").IsEmpty && Hotkey.Parse(null).IsEmpty, "bad hotkeys are empty");
        Check(new Hotkey(Vk.F1 + 11, Modifiers.Ctrl | Modifiers.Alt).ToString() == "Ctrl + Alt + F12", "hotkeys read naturally");
    }

    private static void StringsHaveBothLanguages()
    {
        var missingPt = Strings.En.Keys.Where(k => !Strings.Pt.ContainsKey(k)).ToList();
        var missingEn = Strings.Pt.Keys.Where(k => !Strings.En.ContainsKey(k)).ToList();
        Check(missingPt.Count == 0, "every key has Portuguese: " + string.Join(", ", missingPt));
        Check(missingEn.Count == 0, "every key has English: " + string.Join(", ", missingEn));
        Strings.SetLanguage(Strings.PortugueseBrazil);
        Check(Strings.Get("Play") == "Executar" && Strings.Duration(3200) == "3,2 s", "Portuguese text and numbers");
        Strings.SetLanguage(Strings.English);
        Check(Strings.Duration(850) == "850 ms" && Strings.Duration(125_000) == "2 min 05 s", "durations read naturally");
    }

    private static void HostedArguments()
    {
        var hosted = HostedOptions.TryParse(new[] { "--hosted", "--parent", "42", "--data", @"C:\data dir", "--show" });
        Check(hosted != null && hosted.ParentId == 42 && hosted.DataDirectory == @"C:\data dir" && hosted.Show, "hosted options parse");
        Check(HostedOptions.TryParse(new[] { "--hosted", "--data", "relative" }) == null, "hosted needs a rooted data folder");
        Check(HostedOptions.TryParse(new[] { "--startup" }) == null, "standalone is not hosted");
    }

    private static void LanguageFollowsStk(string folder)
    {
        var stk = Path.Combine(folder, "stk-settings.json");
        Check(LanguageResolver.Resolve("auto", stk) == Strings.English, "no SwissTechKnife means English");
        File.WriteAllText(stk, "{\"updateFrequency\":\"weekly\",\"language\":\"pt-BR\"}");
        Check(LanguageResolver.Resolve("auto", stk) == Strings.PortugueseBrazil, "auto follows SwissTechKnife");
        Check(LanguageResolver.Resolve("en", stk) == Strings.English, "an explicit choice wins");
        File.WriteAllText(stk, "{\"updateFrequency\":\"weekly\"}");
        Check(LanguageResolver.Resolve("auto", stk) == Strings.English, "SwissTechKnife without a language is English");
    }

    private static void FormatterDescribesSteps()
    {
        var press = Step.KeyStep('S', 0, false, KeyAction.Press);
        press.Modifiers = Modifiers.Ctrl;
        Check(StepFormatter.Title(press) == "Press Ctrl + S", "press with modifiers");
        Check(StepFormatter.Title(new Step { Kind = StepKind.Mouse, MouseAction = MouseAction.Scroll, Amount = -360 }) == "Scroll down 3", "scroll in notches");
        Check(StepFormatter.Title(new Step { Kind = StepKind.LoopStart, Count = 0 }) == "Repeat until stopped", "endless loop");
        Check(StepFormatter.Timing(new Step { Kind = StepKind.Delay, Ms = 100, MsMax = 250 }) == "100 ms – 250 ms", "random delay range");
    }

    /// <summary>
    /// Path playback schedules every sample at start + its timestamp. Replaying a 1000 Hz schedule with the real waiter
    /// (no input sent) measures how late each sample lands and that lateness never accumulates.
    /// </summary>
    private static void WaiterKeepsTheRecordedClock()
    {
        using var cancel = new System.Threading.ManualResetEvent(false);
        using var waiter = new Clicket.Input.PreciseWaiter(cancel);
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        var lateness = new List<double>();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 1; i <= 1000; i++)
        {
            var due = start + i * frequency / 1000; // one sample per millisecond, one second in total
            waiter.WaitUntil(due);
            lateness.Add((System.Diagnostics.Stopwatch.GetTimestamp() - due) * 1000.0 / frequency);
        }
        var total = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / frequency;
        lateness.Sort();
        var p50 = lateness[lateness.Count / 2];
        var p99 = lateness[lateness.Count * 99 / 100];
        Console.WriteLine($"path clock: 1000 samples over {total:0.0} ms, lateness p50 {p50:0.000} ms, p99 {p99:0.000} ms, max {lateness[lateness.Count - 1]:0.000} ms");
        // Shared CI runners are noisy neighbours: there only the absence of drift is asserted; locally the precision too.
        var ci = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
        Check(total < (ci ? 1100 : 1010), "a one-second path replays in one second (got " + total.ToString("0.0") + " ms)");
        if (!ci) Check(p99 < 1.0, "99% of samples land within a millisecond");

        cancel.Set();
        var cancelled = System.Diagnostics.Stopwatch.StartNew();
        var completed = waiter.Wait(TimeSpan.FromSeconds(5));
        Check(!completed && cancelled.ElapsedMilliseconds < 50, "a stop interrupts a long wait at once");
    }

    private static void ScriptParametersBecomeFields()
    {
        const string script = "# Rename files\r\n<# block, with param( inside #>\r\n[CmdletBinding()]\r\nparam(\r\n" +
            "    [Parameter(Mandatory = $false, HelpMessage = \"Target folder\")] [string] $Folder = \"$env:USERPROFILE\\Pictures\", # trailing, comment\r\n" +
            "    [ValidateSet('Date', \"Name, then date\")] [string] $Order = 'Date',\r\n" +
            "    [int] $Start = 1,\r\n    [double]$Scale=1.5,\r\n    [switch] $DryRun,\r\n    [bool] $Loud = $true,\r\n    $Plain\r\n)\r\nWrite-Output 'param(fake)'\r\n";
        var p = ScriptParams.Parse(script);
        Check(p.Count == 7, "every declared parameter is found (got " + p.Count + ")");
        if (p.Count != 7) return;
        Check(p[0].Name == "Folder" && p[0].Type == ScriptParamType.Text && p[0].Label == "Target folder" && p[0].Default == "$env:USERPROFILE\\Pictures", "text with label and default");
        Check(p[1].Type == ScriptParamType.Choice && p[1].Choices.Count == 2 && p[1].Choices[1] == "Name, then date" && p[1].Default == "Date", "ValidateSet becomes a list, commas in quotes kept");
        Check(p[2].Type == ScriptParamType.Integer && p[2].Default == "1", "int");
        Check(p[3].Type == ScriptParamType.Number && p[3].Default == "1.5", "double without spaces");
        Check(p[4].Type == ScriptParamType.Toggle && p[4].Default == "false", "switch defaults off");
        Check(p[5].Type == ScriptParamType.Toggle && p[5].Default == "true", "bool keeps $true");
        Check(p[6].Name == "Plain" && p[6].Type == ScriptParamType.Text, "untyped is text");
        Check(ScriptParams.Parse("Write-Output 1").Count == 0 && ScriptParams.Parse("param(").Count == 0 && ScriptParams.Parse(null).Count == 0, "no or broken param() means no fields");

        var values = new Dictionary<string, string> { ["Order"] = "Name, then date", ["Start"] = "7", ["DryRun"] = "true", ["Plain"] = "it's", ["Scale"] = "abc" };
        var call = ScriptParams.BuildInvocation(@"C:\t\s.ps1", p, values);
        Check(call == @"& 'C:\t\s.ps1' -Order 'Name, then date' -Start 7 -DryRun:$true -Plain 'it''s'; exit $LASTEXITCODE",
            "only set values are passed, quoted safely; bad numbers are skipped (got " + call + ")");
    }

    private static void Check(bool condition, string name)
    {
        if (condition) return;
        _failures++;
        Console.WriteLine("FAILED: " + name);
    }
}
