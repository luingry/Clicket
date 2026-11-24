# Clicket as a SwissTechKnife module

Module id `clicket`. Shape: **hosted process**, like SmoothMice, because keyboard and mouse hooks must never share the host's UI thread.

## How it works

- `src/Clicket.Module` is a thin supervisor (`IBackgroundModule`, `IModuleWindow`, `activation: startup`, `loadMode: memory`). It references the SDK compile-only (`lib/SwissTechKnife.Sdk.dll`, never packaged).
- `scripts/pack-module.ps1` builds `artifacts/module/clicket-<version>.zip`: `module.json`, `Clicket.Module.dll` and `app/Clicket.Hosted.exe` (the same exe, renamed so it is never mistaken for the standalone one).
- The module starts `Clicket.Hosted.exe --hosted --parent <host pid> --data <module data folder>` with the host. Hosted mode has no tray icon, no Run key and keeps its macros and settings in the module's data folder. It exits with its parent, or when the module sets `Local\Clicket.Hosted.Exit`.
- The page's **Open Clicket** and a double click on the module's notification-area icon set `Local\Clicket.App.Activate` (or start the app with `--show`).
- Both copies share the `Local\Clicket.SingleInstance` mutex. When the standalone app is open, the hosted copy exits with code 3 and the page shows *Open outside SwissTechKnife*; the module takes over when the standalone app closes (checked every 5 s only in that state).
- Crashes restart the app with a growing delay, at most 3 times a minute, then the page shows *Stopped*.
- Language: the app's "Automatic" setting reads `language` from SwissTechKnife's `settings.json` (`STK_HOME` respected) each time its window comes forward; the module page reads the same file.

## Release

A version bump on `main` (VERSION + CHANGELOG + `module.json` with its `releaseNotes`) publishes `vX.Y.Z` with the installer and `clicket-X.Y.Z.zip`. After the first catalog entry, the host's *Sync catalog* workflow picks up new versions on its own.
