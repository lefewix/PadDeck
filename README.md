# PadDeck

A Stream Deck–style app for a cheap 6-key, 1-knob macropad (CH57x, USB `1189:8840`). Each key runs an action and
shows a small popup that never takes focus, so it works over games.

## The pad

The pad is reprogrammed once (Program pad… in the app, or `PadDeck.exe --program out.txt`) to send **F19–F24**
on its six keys, with the knob on volume down / play-pause / volume up. The layout is stored on the pad
itself. Programming goes through the pad's vendor HID interface (`MI_00`, output report id 3), so no extra driver
is needed; the protocol is from [kriomant/ch57x-keyboard-tool](https://github.com/kriomant/ch57x-keyboard-tool).

A low-level keyboard hook (on its own thread, so a busy UI can never get it removed by Windows) catches
F19–F24. Keys with an action are swallowed, and unset keys pass through so games or OBS can use them.

## Actions

| Action | What it does |
|---|---|
| **Open** | A file, app, folder, URL or script. The new window is brought to the front. Can be an **on/off switch**: give it a "running" check (`llama-server`, or `python in C:\some\folder`) and a stop script, and a press while it's on asks for a second press, then runs the stop script. **GPU-heavy** switches won't start while OBS is live or recording. |
| **Keys** | A key combo or sequence, e.g. `Ctrl+Shift+M` or `Ctrl+C, Ctrl+V`. |
| **Type text** | Types text (newlines become Enter). |
| **Mic mute** | Toggles the default (or a chosen) microphone. |
| **Sound switch** | Cycles the default playback (or recording) device through a list. Devices are found again by name or adapter if Windows gives them a new id. |
| **App mute** | Mutes/unmutes one app's audio. |
| **OBS** | Save replay / toggle recording / toggle streaming over obs-websocket v5 (port and password are read from OBS's own config). If the replay buffer is off, Save replay turns it on, or starts OBS in the tray. After a save, ClipTrimmer adds the clip's cover picture. |
| **Reset** | Two presses within 3 s. Stops the local LLM and ComfyUI, asks every non-core window to close (like clicking X), restarts the core apps marked Restart (never OBS while it's live or recording; the replay buffer is stopped first so OBS doesn't sit on its exit prompt), and starts any core app that's missing. |

Any key can also have a **hold action** (hold ~0.6 s), e.g. open ClipTrimmer's clip list. A key with a hold action
fires its normal action on release.

## Also

- **Game warnings:** when a fullscreen game comes to the front, a popup warns once if a GPU-heavy tool is still
  on or the OBS replay buffer is off, and says which key fixes it.
- **Window placement:** rules that put an app's main window on a given monitor and half whenever it opens
  (e.g. OBS → left monitor, left half).
- **Popups:** bottom or top of a chosen monitor; optional hiding over fullscreen games (warnings and Reset
  confirmations always show).
- PadDeck starts from a fresh sign-in environment, so apps it launches don't inherit variables from whatever
  started it.
- Settings: `%APPDATA%\PadDeck\settings.json`. Each save keeps the previous file as `settings.json.bak`; a
  damaged file is copied aside (`settings.broken-*.json`) and the backup is loaded.
- Autostart: HKCU Run entry with `--tray`.

## Building

Needs the .NET 10 SDK (the app is framework-dependent on the .NET 10 Desktop Runtime).

```powershell
.\build.ps1          # → dist\PadDeck.exe   (quit PadDeck from the tray first)
.\build.ps1 -Test    # also runs the self-tests (130)
```

## Command line

| | |
|---|---|
| `--tray` | start hidden in the tray |
| `--selftest out.txt` | run the self-tests |
| `--program out.txt` | program the pad (F19–F24, knob = volume) |
| `--snapshot ui.png` | render the UI and popups to PNGs without showing a window |
| `--reset-plan out.txt` | read-only: what a Reset would close and start |
| `--obs-ping out.txt` | check the OBS connection (live / recording / replay buffer) |
| `--apply-windows [out.txt]` | place open windows by the rules once |
| `--env out.txt` | the environment PadDeck hands to apps it launches |

## Layout

```
src\Program.cs            entry point, command line, single instance, clean environment
src\AppSettings.cs        settings model + safe load/save
src\Core\KeyHook.cs       F19–F24 hook on its own thread
src\Core\Actions.cs       running actions, hold action
src\Core\Toggle.cs        on/off switches
src\Core\Obs.cs           obs-websocket v5 client
src\Core\Reset.cs         Reset
src\Core\GameWatch.cs     fullscreen game detection + warnings
src\Core\AudioCtl.cs      mic mute, device switching, app mute (NAudio + IPolicyConfig)
src\Core\WindowPlacer.cs  window rules
src\Core\Foreground.cs    focus a newly launched window
src\Core\Pad.cs           pad programming over HID
src\Ui\                   main window, popup, icons, theme
src\Tests.cs              self-tests
```
