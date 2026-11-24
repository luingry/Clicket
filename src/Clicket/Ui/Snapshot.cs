using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clicket.Core;

namespace Clicket.Ui;

/// <summary>--snapshot &lt;dir&gt; [language]: renders every visual state to PNG off-screen, with a sample library. Nothing is saved.</summary>
internal static class Snapshot
{
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var app = (App)Application.Current;
        app.Library = SampleLibrary();
        var suffix = Strings.Language == Strings.English ? "" : "-" + Strings.Language;

        Capture(directory, "main" + suffix + ".png", MainOf(window => window.SelectForSnapshot(0, 2)));
        Capture(directory, "main-path" + suffix + ".png", MainOf(window => window.SelectForSnapshot(2, 1)));
        Capture(directory, "main-text" + suffix + ".png", MainOf(window => window.SelectForSnapshot(1, 0)));
        Capture(directory, "main-loop" + suffix + ".png", MainOf(window => window.SelectForSnapshot(0, 0)));
        Capture(directory, "main-click" + suffix + ".png", MainOf(window =>
        {
            window.SelectForSnapshot(0, 3);
            window.ShowTrashForSnapshot(2, armed: false);
            window.ShowTrashForSnapshot(4, armed: true);
        }));
        Capture(directory, "main-script" + suffix + ".png", MainOf(window => window.SelectForSnapshot(3, 0), 1000));
        Capture(directory, "main-image" + suffix + ".png", MainOf(window => window.SelectForSnapshot(6, 1), 1000));
        Capture(directory, "run-log" + suffix + ".png", SampleLog(app.Library[6]));
        Capture(directory, "main-settings" + suffix + ".png", MainOf(window =>
        {
            window.SelectForSnapshot(0, 2);
            window.OpenSettings(animate: false);
        }));
        app.Library = new List<Macro>();
        Capture(directory, "main-empty" + suffix + ".png", MainOf(window => { }));
        app.Library = SampleLibrary();
        Capture(directory, "recording" + suffix + ".png", new RecordingBar(new Hotkey(Vk.F1 + 8, Modifiers.Ctrl)));
        Capture(directory, "pad" + suffix + ".png", new PadWindow());
        Capture(directory, "tray-menu" + suffix + ".png", new TrayMenuWindow());
    }

    private static Window MainOf(Action<MainWindow> prepare, double height = 740)
    {
        var window = new MainWindow { Width = 1180, Height = height };
        window.ContentRendered += (s, e) => { };
        window.Loaded += (s, e) => prepare(window);
        return window;
    }

    private static void Capture(string directory, string name, Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -10000;
        window.Show();
        // Let bindings, deferred focus and the first layout settle before rendering.
        for (var i = 0; i < 3; i++)
        {
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();
        }
        var root = (FrameworkElement)window.Content;
        var (width, height) = ((int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right), (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom));
        var content = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        content.Render(root);
        window.Close();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Artwork.Render(width, height, dc =>
        {
            dc.DrawRectangle(Artwork.Token<Brush>("Brush.Backdrop"), null, new Rect(0, 0, width, height));
            dc.DrawImage(content, new Rect(0, 0, width, height));
        })));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static List<Macro> SampleLibrary()
    {
        var farm = new Macro { Name = "Gather & sell loop", Hotkey = new Hotkey(Vk.F1 + 5, Modifiers.None), Mode = PlayMode.Toggle, OnPad = true };
        farm.Steps.AddRange(new[]
        {
            new Step { Kind = StepKind.LoopStart, Count = 0 },
            Step.KeyStep('E', 0, false, KeyAction.Press),
            Step.DelayStep(250),
            Click(MouseButton.Left),
            new Step { Kind = StepKind.Delay, Ms = 1200, MsMax = 1500 },
            Step.KeyStep('S', 0, false, KeyAction.Press),
            Step.DelayStep(80),
            new Step { Kind = StepKind.LoopEnd },
        });
        farm.Steps[5].Modifiers = Modifiers.Ctrl;

        var signature = new Macro { Name = "Email signature", Hotkey = new Hotkey('S', Modifiers.Ctrl | Modifiers.Alt), OnPad = true };
        signature.Steps.Add(new Step { Kind = StepKind.Text, Text = "Best regards,\nLuana — Clicket team", CharDelay = 4 });
        signature.Steps.Add(Step.KeyStep(Vk.Return, 0, false, KeyAction.Press));

        var drag = new Macro { Name = "Drag file to upload", Hotkey = new Hotkey(Vk.XButton2, Modifiers.None) };
        drag.Steps.Add(MoveTo(640, 420));
        drag.Steps.Add(new Step { Kind = StepKind.Path, PathData = PathCodec.Encode(Wave(640, 420, 1180, 380, 900)) });
        drag.Steps.Add(Down(MouseButton.Left));
        drag.Steps.Add(new Step { Kind = StepKind.Path, PathData = PathCodec.Encode(Wave(1180, 380, 1460, 610, 640)) });
        drag.Steps.Add(Step.DelayStep(120));
        drag.Steps.Add(new Step { Kind = StepKind.Mouse, MouseAction = MouseAction.Up });

        var dns = new Macro { Name = "Flush DNS", Mode = PlayMode.Once };
        dns.Steps.Add(new Step { Kind = StepKind.Command, Command = "ipconfig /flushdns", Hidden = true, Wait = true });

        var dash = new Macro { Name = "Morning dashboards", Hotkey = new Hotkey('D', Modifiers.Win | Modifiers.Shift), OnPad = true };
        dash.Steps.Add(new Step { Kind = StepKind.Open, Target = "https://github.com/luingry/Clicket" });
        dash.Steps.Add(Step.DelayStep(1500));
        dash.Steps.Add(new Step { Kind = StepKind.Mouse, MouseAction = MouseAction.Scroll, Amount = -360 });

        var rename = new Macro { Name = "Rename screenshots", Hotkey = new Hotkey('N', Modifiers.Ctrl | Modifiers.Shift) };
        var script = new Step
        {
            Kind = StepKind.Script,
            Wait = true,
            Hidden = true,
            Script = "# Rename screenshots in order\r\nparam(\r\n" +
                     "    [Parameter(HelpMessage = \"Folder\")] [string] $Folder = \"$env:USERPROFILE\\Pictures\\Screenshots\",\r\n" +
                     "    [Parameter(HelpMessage = \"Name prefix\")] [string] $Prefix = \"shot\",\r\n" +
                     "    [Parameter(HelpMessage = \"Order by\")] [ValidateSet('Date', 'Name')] [string] $Order = 'Date',\r\n" +
                     "    [Parameter(HelpMessage = \"First number\")] [int] $Start = 1,\r\n" +
                     "    [Parameter(HelpMessage = \"Only show what would change\")] [switch] $DryRun\r\n)\r\n\r\n" +
                     "$i = $Start\r\nGet-ChildItem $Folder -Filter *.png | Sort-Object $(if ($Order -eq 'Date') { 'LastWriteTime' } else { 'Name' }) | ForEach-Object {\r\n" +
                     "    $name = '{0}-{1:000}.png' -f $Prefix, $i++\r\n    if (-not $DryRun) { Rename-Item $_.FullName $name }\r\n}\r\n",
        };
        script.SetScriptValue("Prefix", "invoice");
        script.SetScriptValue("Start", "100");
        rename.Steps.Add(script);
        rename.Steps.Add(new Step { Kind = StepKind.Text, Text = "Done ✔" });

        var accept = new Macro { Name = "Accept the match", Hotkey = new Hotkey(Vk.F1 + 7, Modifiers.None), Mode = PlayMode.Toggle };
        accept.Steps.AddRange(new[]
        {
            new Step { Kind = StepKind.LoopStart, Count = 0, LoopPauseMs = 500 },
            new Step { Kind = StepKind.ImageStart, ImageData = SampleButton(), ClickImage = true, Threshold = 92, IntervalMs = 300 },
            Step.DelayStep(400),
            Step.KeyStep(Vk.Return, 0, false, KeyAction.Press),
            new Step { Kind = StepKind.ImageEnd },
            new Step { Kind = StepKind.LoopEnd },
        });

        return new List<Macro> { farm, signature, drag, rename, dns, dash, accept };
    }

    /// <summary>A button-like picture for the image step, drawn here so the snapshot needs no file.</summary>
    private static string SampleButton()
    {
        var bitmap = Artwork.Render(112, 36, dc =>
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x3E, 0x9B, 0x5A)), null, new Rect(0, 0, 112, 36), 8, 8);
            var text = new FormattedText("ACCEPT", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 14, Brushes.White, 1.0);
            dc.DrawText(text, new Point((112 - text.Width) / 2, (36 - text.Height) / 2));
        });
        return Input.ImageCodec.Encode(Input.ImageCodec.FromSource(bitmap));
    }

    /// <summary>A minute of that macro's execution, as the log window shows it while it runs.</summary>
    private static Window SampleLog(Macro macro)
    {
        var log = RunLogs.For(macro.Id);
        log.Clear();
        var second = System.Diagnostics.Stopwatch.Frequency;
        var t = System.Diagnostics.Stopwatch.GetTimestamp() - 20 * second;
        var image = macro.Steps[1];
        log.Add(t, RunEventKind.Started, value: 0);
        for (var i = 0; i < 4; i++) log.Add(t += second * 3 / 10, RunEventKind.ImageMissed, 1, image, null, 0.41 + i * 0.03, 0, 0, 38 + i);
        log.Add(t += second * 3 / 10, RunEventKind.ImageFound, 1, image, "Click", 0.97, 1712, 948, 41);
        log.Add(t += second / 1000, RunEventKind.Step, 2, macro.Steps[2]);
        log.Add(t += second * 4 / 10, RunEventKind.Step, 3, macro.Steps[3]);
        for (var i = 0; i < 3; i++) log.Add(t += second / 2, RunEventKind.ImageMissed, 1, image, null, 0.38, 0, 0, 36);
        log.Add(t += second / 3, RunEventKind.Warning, 1, image, "Access is denied.");
        log.Add(t += second / 3, RunEventKind.ImageFound, 1, image, "Click", 0.95, 1712, 950, 40);
        log.Add(t += second / 1000, RunEventKind.Step, 2, macro.Steps[2]);
        log.Add(t += second * 4 / 10, RunEventKind.Step, 3, macro.Steps[3]);
        log.Add(t += second / 2, RunEventKind.Finished, value: 2);
        return new RunLogWindow(macro);
    }

    private static Step Click(MouseButton button) => Step.MouseStep(MouseAction.Click, button);

    private static Step Down(MouseButton button) => Step.MouseStep(MouseAction.Down, button);

    private static Step MoveTo(int x, int y)
    {
        var step = Step.MouseStep(MouseAction.MoveTo);
        step.X = x;
        step.Y = y;
        return step;
    }

    /// <summary>A hand-like path: eased travel with a little wobble, sampled at 500 Hz.</summary>
    private static List<PathPoint> Wave(int x0, int y0, int x1, int y1, int millis)
    {
        var points = new List<PathPoint>();
        var samples = millis / 2;
        for (var i = 1; i <= samples; i++)
        {
            var t = i / (double)samples;
            var eased = t * t * (3 - 2 * t);
            var wobble = Math.Sin(t * Math.PI * 3) * 14 * (1 - t);
            points.Add(new PathPoint((int)(x0 + (x1 - x0) * eased), (int)(y0 + (y1 - y0) * eased + wobble), i * 2000L));
        }
        return points;
    }
}
