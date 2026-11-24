# Clicket design system

## Idea

**A cricket's chirp: a rhythm of small, exact clicks in time.** A macro is a rhythm too, so the editor shows it as one: every step is a beat with its family's hue, and its timing bar is as long as the time it takes. Long waits, quick taps and recorded movements read at a glance, before any text.

The look follows SwissTechKnife (warm, tonal dark surfaces, no strokes) so Clicket feels at home beside it, with its own accent: cricket green.

## Where the design does work

- **Timing bars.** Each step's bar is its share of the macro's longest step, on a square-root scale so one 30 s wait does not flatten every 50 ms tap. The bar's color is the step family.
- **Family hues.** Keyboard green, mouse teal, text amber, delay grey, loop violet, command orange, open blue. The Insert tiles and the timeline glyphs use the same hue, so a new step visibly lands where it came from.
- **Loop indentation.** Steps inside a loop are indented by depth, and loop markers are violet and bold, so structure needs no brackets.
- **The pad never takes focus**, so it removes the "switch back to my window" step.

## Principles

1. **Tone, not strokes.** Canvas → Surface1 (side panes, timeline) → Surface2 (cards, dialogs) → Surface3 (inputs, selected rows) → Surface4/5 (secondary buttons, popups, tracks).
2. **Green means go.** The accent marks the primary action (Play), the selection, switches that are on and what is playing. Nothing else is green, except the keyboard family hue, which is the cricket's own.
3. **Red means recording or destructive.** Record buttons and the recording bar use `Record`; delete uses `Danger` and confirms on a second click.
4. **Show only what applies.** The inspector shows the fields of the selected step kind only; the repeat count appears only in Repeat mode; standalone-only settings hide when hosted.
5. **Motion without displacement.** Hover fades overlays, the selection bar grows in place, the segmented indicator travels, switches slide; nothing moves layout.

## Tokens

`src/Clicket/Theme/Tokens.xaml` holds every value.

| Group | Decision |
|---|---|
| Surfaces | SwissTechKnife's warm dark ramp (`#141413` → `#3D3B36`) for a family resemblance |
| Text | Primary `#F3F0EA`, secondary, tertiary, disabled: warm greys |
| Accent | Cricket green `#8FD14F` with hover/pressed/soft/focus; dark green `#13200A` ink on it |
| Meaning | Record red, danger, warning; status only |
| Step families | One hue per family plus a 14% soft tint for glyph chips |
| Interaction | White overlays for hover/pressed; accent focus ring |
| Type | Segoe UI Variable (Text for UI, Display for titles) and Segoe Fluent Icons for glyphs; sizes 11/12/13/15/20/24 by role; tabular numerals for timings |
| Space | 4 px grid; page 24, panes 16 |
| Radius | 6/8/12/16; chips and switches are half their height |
| Size | Controls 28/34/40, rows 40, library 264, inspector 316 |
| Motion | Fast 120 ms (hover in), Normal 200 ms (hover out, travel), Slow 280 ms (entrances), Pulse 900 ms (recording/playing dots) |
| Elevation | Popup and Overlay shadows, floating layers only |

## Components

| Component | Key | Use |
|---|---|---|
| Buttons | `Button.Primary`, `Button.Secondary` (implicit), `Button.Ghost`, `Button.Danger`, `Button.Record`, `Button.Icon(.Sm)`, `Toggle.Icon` | Role decides the look; one primary per surface |
| Insert tile | `Button.Tile` + `ui:UiProps.Icon` + `ui:UiProps.Accent` | The seven step families |
| Segmented | `ui:Segmented` with `Keys` (localization keys) | 2–4 exclusive choices with a travelling indicator |
| Switch | `ui:Switch` + `Switch` style | Settings that apply at once; the knob slides |
| Capture field | `ui:KeyCaptureBox` | Hotkeys and key steps; pauses global hotkeys while focused |
| Rows | `ListItem.Row` | Library and timeline; tonal selection with a growing accent bar |
| Floating layer | `ui:FloatingWindow` | Recording bar and pad: no activation, manual drag, clamped to the monitor |

## Icon

`Ui/Artwork.cs` draws the mark: a stylized cricket in profile, mid-chirp, in dark ink on a green tile. Below 32 px the small legs and chirp arcs drop out so it still reads in the tray. `scripts/build.ps1` exports every raster from that code; never edit the PNG or ICO.
