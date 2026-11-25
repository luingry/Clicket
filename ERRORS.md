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

## Click macro capped at ~65 CPS and slowing toward 35 (user report, 1.0.0)

- **Evidence:** every editor-built click/press held the button for a fixed 15 ms before releasing, so one click per pass could never exceed ~66/s. A probe ruled out timer throttling in the background: a windowless process outside the agent package kept 67 waits/s of 15 ms for 20 s, with and without the power-throttling opt-out.
- **Fix (1.0.1):** presses and clicks are instant by default (down and up in one `SendInput` call, as AutoHotkey's SendInput mode does), with a per-step "Hold time (ms)". Unlimited runs are still capped at one pass per millisecond.
- **Not yet explained:** the decline from 65 to 35. Deduction only: something on the machine slowing `SendInput` (another app's low-level hook) or the CPS page itself. Each run now logs its rate, `SendInput` average/max and wait lateness to `clicket.log`; read that line from a slow run before changing anything else.

## Held hotkey restarted "While held" macros every ~67 ms (1.0.1–1.0.2, user report "still 55 CPS")

- **Evidence:** the user's `clicket.log` showed runs of 0.04 s, 0 passes, one after another, while F13 was held; and `SendInput` averaging 10–13 ms per call.
- **Cause:** the 1.0.1 secure-desktop fix asked `GetAsyncKeyState` whether a swallowed hotkey was still down. A key the hook swallows never reaches Windows' key state, so it always reads "up", and every auto-repeat looked like a new press after a lost release: stop, then start.
- **Fix (1.0.3):** a lost release is detected by silence instead: a repeat more than 1.5 s after the previous one (auto-repeat runs every ~33 ms after at most 1 s).
- **Open:** the 10–13 ms per `SendInput` comes from some other process's low-level hook (Windows waits for every hook before returning). It caps clicks near 75/s on that machine; identify it by measuring with suspects closed.
- **Measured (authorized probe, real desktop):** injected mouse move 0.08 ms, key down+up 0.9 ms, **left button down 10.3 ms** (up ~0.1 ms). A low-level mouse hook in another process does ~10 ms of work per button press; Clicket cannot bypass it (neither can AutoHotkey).
- **Root cause found (2026-11-24, authorized elimination on the real desktop):** the AqTracker module (SwissTechKnife, `AqTracker.Hosted.exe`). With everything else running, removing only AqTracker took an injected left click from 11 ms to 0.38 ms; with a fresh AqTracker the p50 starts at 0.5 ms and the p90 climbs back to ~10 ms within seconds, matching the "65 then down to 35 CPS" report. AqTracker has no low-level hook in its source, so the mechanism inside it is still unknown (to investigate in its own repository). Ruled out by measurement: Roblox, ShareX, PowerToys, SmoothMice, WinputLan (+ cursor guard), PadTide, the SwissTechKnife host, Clicket itself, thread message queue, DPI context, process priority. AutoHotkey measured on the same desktop hit the same 75 CPS ceiling, so it is not a Clicket limitation.

## Image search matched the wrong place for one-colour pictures (found in tests, before release of 1.1.2)

- **Symptom:** a solid-colour picture was "found" elsewhere on the screen; a recoloured copy of a pattern still scored above 90 %.
- **Cause:** the coarse pyramid scan scored brightness only, so every even area of the same grey competed with the real one and the true place could fall out of the top candidates before colour was ever checked.
- **Fix:** a picture whose channels are all even (std < 2) is searched on a "distance to its colour" image instead of brightness, so colour decides from the first level. Patterned pictures keep correlation on brightness, capped by the average-colour difference at full size.
- **Avoid:** synthetic test screens must not be periodic, or a wrong place matches as well as the right one; `MatcherFindsThePicture` uses random panels plus noise.

## Mouse wheel scrolled far too much in every Clicket list and panel (user report on a pre-release build of 1.1.2)

- **Evidence:** same failure SwissTechKnife fixed in 0.4.9 (its `ERRORS.md`, 2026-10-26); SmoothMice runs on the user's machine. A probe raising 17 wheel events of delta -7 (about one notch) on a Clicket-styled `ScrollViewer`, with layout between them as on screen, moved 816 px instead of 48.
- **Cause:** WPF's `ScrollViewer` scrolls a fixed step per wheel message and ignores `MouseWheelEventArgs.Delta`; smooth-scrolling tools, high-resolution wheels and precision touchpads send many fractional deltas.
- **Fix (1.1.2):** `Ui/WheelScrolling` (ported from STK) registers a class handler on `ScrollViewer` in `App.OnStartup` that scales the scroll by `Delta / 120`: exact pixels in pixel mode, a carried remainder in item mode; whole notches keep WPF's path. Same probe after the fix: 47.6 px.
- **Avoid:** every WPF app of this family needs this handler from the start; a probe without a layout pass between messages hides the bug (it showed 48 px before the fix).

## Recording captured nothing until the user clicked outside Clicket (user report, 1.1.2)

- **Evidence (code):** `Engine.Record` drops every key-down while `GetForegroundWindow()` belongs to Clicket and every click or wheel over a Clicket window (so the stop button and the recording bar stay out of the macro). `App.BeginRecording` left the editor in front, only disabled, so typing and clicking went to it and were all dropped until another window took the foreground.
- **Fix (1.1.3):** `MainWindow.OnRecordingStateChanged(true)` minimizes a visible editor, which hands the foreground to the window underneath; stopping restores the state it had (normal or maximized) before `ShowMainWindow` brings it forward.
- **Avoid:** any surface that starts a recording must leave the foreground to the target app; a Clicket window in front is a recording that silently discards input.
