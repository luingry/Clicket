# Agent guide

## Product

Clicket records, builds and replays keyboard and mouse macros, AutoHotkey-light with a Synapse-style editor.

- **One job:** a hotkey plays a macro, exactly as recorded or built.
- **At idle:** nothing runs. The keyboard hook thread sleeps in `GetMessage`; the mouse hook exists only while recording or when a macro uses a mouse-button hotkey; no timers.
- **Surfaces:** tray icon and styled tray menu (standalone), the editor window, the recording bar, the macro pad (non-activating), and the SwissTechKnife module page.
- **Two shapes, one exe:** standalone (tray, Run key, installer) and hosted by the SwissTechKnife module (`--hosted --parent --data [--show]`; the host owns tray and start-up). See `docs/SWISSTECHKNIFE.md`.

Clicket is C#/WPF on .NET Framework 4.8, with nothing beyond what Windows already ships.

## Rules

- **Lightness.** With nothing to do, the app does nothing. A new package, service or resident runtime needs the owner's approval.
- **Input path.** Hook callbacks only read, check the frozen trigger set and enqueue. Everything else happens on the dispatch thread or a runner thread. Playback uses `SendInput` with Clicket's `dwExtraInfo` signature (hooks ignore it) and `PreciseWaiter` for timing; never `Thread.Sleep`. Every runner releases what it pressed when it ends.
- **Recorded paths are lossless.** Every distinct hook sample is kept (`PathCodec`, delta varints) and replayed on its original clock (deadline = start + timestamp), so lateness never accumulates.
- **Language.** Code, comments, docs, commit messages and the CHANGELOG in English. UI text in English and Brazilian Portuguese through `Core/Strings.cs` (`{ui:Loc Key}` in XAML, `Strings.Get/Format` in code); the tests fail when a key misses a language. Never hardcode UI text. Every `.ps1` and the `.iss` stay pure ASCII.
- **Tokens only.** Every color, size, radius and duration comes from `src/Clicket/Theme/Tokens.xaml`, every style from `Theme/Controls.xaml`. No native-looking controls, no solid borders, every state change animated (opacity/transform) and honoring `SystemParameters.ClientAreaAnimation`. `docs/DESIGN_SYSTEM.md` records the reasoning.
- **Diagnostics.** `--snapshot <dir> [en|pt-BR]` and `--export-icon` run before the mutex and never persist anything. Only the app's own value in the `Run` key may change, and the registry is the truth for it.
- **Test harnesses** must not drive `SendInput`, the cursor or global hooks against the real desktop without the owner's explicit OK.

## Versions

- `VERSION` is the single source, SemVer without `v`. Props, installer and `scripts/pack-module.ps1` read it; `src/Clicket.Module/module.json` must carry the same version and a `releaseNotes` entry for it (the script refuses otherwise).
- Every delivered change bumps it and adds a `## [X.Y.Z]` section to `CHANGELOG.md` (Keep a Changelog), with short user-facing notes.
- A push to `main` that changes `VERSION` or `CHANGELOG.md` publishes `vX.Y.Z` with the installer and `clicket-X.Y.Z.zip`; the SwissTechKnife catalog sync picks the module up. A published version is immutable. The workflow keeps the newest 3.

## Validation

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
src/Clicket/bin/Release/net48/Clicket.exe --snapshot snapshots en
src/Clicket/bin/Release/net48/Clicket.exe --snapshot snapshots pt-BR
```

Open the snapshot images for any visual change and refresh `docs/images` when the UI changes. Record non-trivial fixes in `ERRORS.md`.
