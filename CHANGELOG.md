# Changelog

All notable changes to Clicket are recorded here, in the [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format, with [semantic versioning](https://semver.org/).

## [1.0.1]

### Changed

- Key presses and clicks built in the editor are instant by default, like AutoHotkey: a Toggle click macro now reaches hundreds of clicks per second instead of about 65. A new "Hold time (ms)" field keeps a press down for games that need it.
- Recording starts with no hotkey, so Clicket never takes a combination another app already uses; pick one in Settings. The old Ctrl + Alt + R default is removed from existing settings.

### Added

- A trash button on each macro in the list when you point at it; click it twice to delete.
- Each run writes one line to the log with its rate and timing, to explain any slowdown.

## [1.0.0]

### Added

- Record keyboard, mouse buttons, the wheel and mouse movement, with real delays; movement keeps every sample and replays on its original clock.
- Build macros step by step: keyboard, mouse, text, delay, loop, command and open steps, edited in place.
- Script steps that run your own PowerShell; the parameters its param() block declares appear as fields in the editor.
- Global hotkeys (keys or mouse side buttons) that play once, repeat, repeat while held or toggle; one hotkey stops everything.
- Macro pad that plays macros without taking focus, plus undo, copy and paste of steps, and import/export of macros.
- English and Brazilian Portuguese, following SwissTechKnife automatically.
- SwissTechKnife module that runs Clicket in its own process.
