using System;
using System.Windows.Media;
using Clicket.Core;

namespace Clicket.Ui;

/// <summary>A macro as the library list shows it.</summary>
public sealed class MacroItem : Observable
{
    private bool _isRunning;
    private bool _isVisible = true;

    public MacroItem(Macro macro) => Macro = macro;

    public Macro Macro { get; }

    public string Name => Macro.Name.Length == 0 ? Strings.Get("NewMacroName") : Macro.Name;

    public string HotkeyText => Macro.Hotkey.IsEmpty ? Strings.Get("NoHotkey") : Macro.Hotkey.ToString();

    public bool HasHotkey => !Macro.Hotkey.IsEmpty && Macro.Enabled;

    public string Summary => Macro.Steps.Count == 1 ? Strings.Get("StepCountOne") : Strings.Format("StepCount", Macro.Steps.Count);

    public bool OnPad => Macro.OnPad;

    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    /// <summary>Filtered out by the search box.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    public bool Matches(string filter) =>
        filter.Length == 0 || Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
        HotkeyText.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0;

    public void Refresh() => RaiseAll();
}

/// <summary>A step as the timeline shows it: glyph and hue of its family, text, and a bar sized by its duration.</summary>
public sealed class StepItem : Observable
{
    private int _index;
    private int _depth;
    private double _bar;
    private bool _isError;

    public StepItem(Step step) => Step = step;

    public Step Step { get; }

    public int Index
    {
        get => _index;
        set { if (Set(ref _index, value)) Raise(nameof(Number)); }
    }

    public string Number => (Index + 1).ToString(Strings.Culture);

    public int Depth
    {
        get => _depth;
        set { if (Set(ref _depth, value)) Raise(nameof(Indent)); }
    }

    public System.Windows.Thickness Indent => new System.Windows.Thickness(Depth * Artwork.Token<double>("Space.IndentStep"), 0, 0, 0);

    public string Title => StepFormatter.Title(Step);

    public string? Note => string.IsNullOrWhiteSpace(Step.Note) ? null : Step.Note;

    public string? Detail
    {
        get
        {
            if (Step.Kind == StepKind.Path)
            {
                var info = PathCodec.Measure(Step.PathData);
                return Strings.Format("DescPathDetail", info.Count);
            }
            if (Step.Kind == StepKind.Script)
            {
                var count = ScriptParams.Parse(Step.Script).Count;
                return count == 0 ? null : Strings.Format("DescScriptParams", count);
            }
            return null;
        }
    }

    public string DetailText => Detail == null ? "" : "   " + Detail;

    public string NoteText => Note == null ? "" : "   " + Note;

    public string Timing => StepFormatter.Timing(Step);

    public long Millis => StepFormatter.Millis(Step);

    /// <summary>0..1 share of the timing bar, scaled against the longest step of the macro.</summary>
    public double Bar
    {
        get => _bar;
        set { if (Set(ref _bar, value)) Raise(nameof(BarWidth)); }
    }

    public double BarWidth => Math.Max(Millis > 0 ? 3 : 0, Bar * Artwork.Token<double>("Size.TimingBar"));

    public bool IsError
    {
        get => _isError;
        set => Set(ref _isError, value);
    }

    public string Glyph => Artwork.Token<string>(Step.Kind == StepKind.Path ? "Glyph.Path" : "Glyph." + StepFormatter.CategoryKey(Step.Kind));

    public Brush Hue => Artwork.Token<Brush>("Brush.Cat." + StepFormatter.CategoryKey(Step.Kind));

    public Brush HueSoft => Artwork.Token<Brush>("Brush.Cat." + StepFormatter.CategoryKey(Step.Kind) + ".Soft");

    public bool IsLoopMarker => Step.Kind == StepKind.LoopStart || Step.Kind == StepKind.LoopEnd;

    public void Refresh() => RaiseAll();
}
