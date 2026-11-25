# Changelog

All notable changes to Clicket are recorded here, in the [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format, with [semantic versioning](https://semver.org/).

## [1.1.3]

### Fixed

- Recording starts right away: the editor now minimizes when you press Record, so the keys and clicks you make in the app underneath are captured without first clicking outside Clicket. Stopping brings the editor back as it was.

## [1.1.2]

### Added

- Image block: looks for a picture on every monitor and, once the match reaches the threshold you set, runs the steps inside it. Optionally clicks the picture first. A check interval sets how often it looks again, and an optional time limit skips the block when the picture never shows up.
- Capture the picture straight from the screen by dragging around it, paste the one copied with Ctrl + C, or open an image file; **Test now** shows how well it matches the screen right now.
- Run log: a small window, opened from the clock button next to Play, shows the last 60 seconds of a macro's execution live, with every step, image search and problem, to the millisecond.

### Changed

- The warning for a block without its end now covers image blocks as well as loops.

### Fixed

- The mouse wheel no longer scrolls Clicket's lists and panels far too much with smooth-scrolling tools (such as SmoothMice), high-resolution wheels or precision touchpads: each small wheel step now scrolls its share of a notch instead of a whole one.

## [1.0.3]

### Fixed

- Holding a hotkey no longer stops and restarts a "While held" macro on every key repeat (and no longer flips a "Toggle" macro on and off), which cut clicks per second roughly in half in 1.0.1 and 1.0.2.

## [1.0.2]

### Added

- Loops have a "Pause between rounds (ms)" setting, 1 ms at minimum, shown next to the loop in the step list.

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
