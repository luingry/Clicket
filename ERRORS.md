# Errors and fixes

## Snapshot run rendered one screen, then failed with `RenderTargetBitmap pixelWidth must be > 0`

- **Cause:** closing the first `MainWindow` in `--snapshot` ran the window's close path, which called `App.Quit()` and shut the dispatcher down; later windows never laid out.
- **Fix:** `App.Quit()` is a no-op unless the process is resident (`_persist`), and `ShouldHideOnClose` is false for diagnostics.
- **Avoid:** diagnostics must not reach lifecycle code; check `IsResident` before quitting, saving or reading user state.

## Snapshots came out in Portuguese when English was requested

- **Cause:** `MainWindow.Activated` re-resolves an "auto" language from SwissTechKnife's settings; it ran during the snapshot.
- **Fix:** only resident runs re-resolve the language.

## Hotkey field rendered as a white native text box

- **Cause:** an implicit `Style TargetType="TextBox"` does not apply to a subclass (`KeyCaptureBox`).
- **Fix:** `<Style TargetType="ui:KeyCaptureBox" BasedOn="{StaticResource {x:Type TextBox}}" />` in `Controls.xaml`.
- **Avoid:** every custom control derived from a styled control needs its own implicit style.

## Importing a non-JSON file threw instead of being refused

- **Cause:** `DataContractJsonSerializer` reports malformed JSON as `System.Xml.XmlException`, not `SerializationException`.
- **Fix:** `MacroStore` catches `XmlException` on load and import.

## CS0108 hides inherited member (`App.Exit`, `StepEditor.TextInput`, `x:Name="Handle"`)

- **Cause:** names that collide with WPF members (`Application.Exit`, `UIElement.TextInput`, a base `Handle` property) fail under `TreatWarningsAsErrors`.
- **Fix:** renamed to `Quit`, `TextField`, `DragArea`.

## Hotkeys misfire after Win+L, Ctrl+Alt+Del or a UAC prompt (found in review, before release)

- **Cause:** the secure desktop hides key-ups from low-level hooks; tracked modifier state stayed "held", and a swallowed trigger key stayed marked as suppressed.
- **Fix:** `InputHook` re-reads the modifiers with `GetAsyncKeyState` on every non-modifier key-down (and mouse-trigger down), and treats a "repeat" of a swallowed key that Windows reports as up as a new press after a lost release.

## Endless macro of instant steps pegged a core (found in review)

- **Cause:** only empty macros and endless loop steps were throttled, not whole passes.
- **Fix:** an unlimited run waits 1 ms after any pass that took under 1 ms.

## Recording kept the stop hotkey's modifiers when the mouse moved (found in review)

- **Cause:** `Finish` stripped dangling downs only from the tail, and a flushed path step stopped the strip.
- **Fix:** every press still held at the end is removed wherever it sits; releases always reach the builder (it drops unmatched ones); a click before any motion gets a Move to.

## Script step: temp file leak and a closed-handle wait (found in review)

- **Cause:** `EnableRaisingEvents` before the `Exited` handler, and the handler disposing the process the runner was waiting on.
- **Fix:** waiting runs own the process and clean up themselves; non-waiting runs attach `Exited` first.
