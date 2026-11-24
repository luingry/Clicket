# Changelog

All notable changes to Clicket are recorded here, in the [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format, with [semantic versioning](https://semver.org/).

## [1.0.0]

### Added

- Record keyboard, mouse buttons, the wheel and mouse movement, with real delays; movement keeps every sample and replays on its original clock.
- Build macros step by step: keyboard, mouse, text, delay, loop, command and open steps, edited in place.
- Script steps that run your own PowerShell; the parameters its param() block declares appear as fields in the editor.
- Global hotkeys (keys or mouse side buttons) that play once, repeat, repeat while held or toggle; one hotkey stops everything.
- Macro pad that plays macros without taking focus, plus undo, copy and paste of steps, and import/export of macros.
- English and Brazilian Portuguese, following SwissTechKnife automatically.
- SwissTechKnife module that runs Clicket in its own process.
