<div align="center">

<img src="assets/icon.png" width="88" alt="PadDeck icon">

# PadDeck

**Turn a $15 macropad into a Stream Deck.**

![Windows](https://img.shields.io/badge/Windows-0078D6?style=flat-square&logo=windows&logoColor=white)
![C#](https://img.shields.io/badge/C%23-.NET_10-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-512BD4?style=flat-square)
![License](https://img.shields.io/badge/license-MIT-lightgrey?style=flat-square)

</div>

A Windows app for the cheap 6-key, 1-knob CH57x macropad (USB `1189:8840`). Each key runs an action and
shows a small popup that never steals focus, so it works on top of games.

## Features

- **Reprograms the pad itself** over HID, so no vendor software or extra driver is needed
- **8 action types**: open apps, key combos, typed text, mic mute, audio device switching, per-app mute,
  OBS control, and a one-press "reset" for your desktop
- **OBS integration** over obs-websocket v5: save replays, toggle recording and streaming
- **On/off switches** that check whether a tool is running and need a second press to confirm
- **Hold actions**: a second action on each key when held for ~0.6 s
- **Game-aware**: warns when a fullscreen game starts while a GPU-heavy tool is still running
- **Window rules**: put an app's window on a chosen monitor and half every time it opens

## Install

1. Download or clone this repo and build it (see [Development](#development))
2. Run `dist\PadDeck.exe` and click **Program pad…** once. The pad then sends F19–F24, and the knob
   controls volume.

## Usage

1. Pick a key in the app and choose an action
2. Press the key: the action runs and a popup confirms it
3. Keys with no action pass through, so games and OBS can still use them

<details>
<summary><b>All actions</b></summary>

| Action | What it does |
|---|---|
| **Open** | A file, app, folder, URL or script, brought to the front. Can be an **on/off switch** with a "running" check and a stop script; **GPU-heavy** switches won't start while OBS is live or recording. |
| **Keys** | A key combo or sequence, e.g. `Ctrl+Shift+M` or `Ctrl+C, Ctrl+V`. |
| **Type text** | Types text (newlines become Enter). |
| **Mic mute** | Toggles the default (or a chosen) microphone. |
| **Sound switch** | Cycles the default playback or recording device. Devices are found again by name if Windows gives them a new id. |
| **App mute** | Mutes/unmutes one app's audio. |
| **OBS** | Save replay / toggle recording / toggle streaming. Reads the port and password from OBS's own config. Turns the replay buffer on if needed. |
| **Reset** | Two presses. Stops GPU tools, politely closes non-core windows, restarts chosen core apps and starts any that are missing. Never touches OBS while it's live. |

</details>

<details>
<summary><b>Command line</b></summary>

| Flag | What it does |
|---|---|
| `--tray` | Start hidden in the tray |
| `--selftest out.txt` | Run the self-tests |
| `--program out.txt` | Program the pad (F19–F24, knob = volume) |
| `--snapshot ui.png` | Render the UI and popups to PNG without showing a window |
| `--reset-plan out.txt` | Read-only: what a Reset would close and start |
| `--obs-ping out.txt` | Check the OBS connection |
| `--apply-windows [out.txt]` | Place open windows by the rules once |
| `--env out.txt` | The environment PadDeck hands to apps it launches |

</details>

## How it works

The pad is programmed through its vendor HID interface (`MI_00`, output report 3), using the protocol from
[kriomant/ch57x-keyboard-tool](https://github.com/kriomant/ch57x-keyboard-tool). The layout is stored on
the pad. A low-level keyboard hook on its own thread catches F19–F24, so a busy UI can never get it removed
by Windows.

<details>
<summary><b>More details</b></summary>

- Apps are launched from a fresh sign-in environment, so they don't inherit variables from whatever started
  PadDeck.
- Settings live in `%APPDATA%\PadDeck\settings.json`. Each save keeps a `.bak`; a damaged file is set aside
  and the backup is loaded.
- Popups sit at the top or bottom of a chosen monitor and can hide over fullscreen games (warnings and
  Reset confirmations always show).
- Autostart is an HKCU Run entry with `--tray`.

```
src\Program.cs            entry point, command line, single instance
src\AppSettings.cs        settings model + safe load/save
src\Core\KeyHook.cs       F19–F24 hook on its own thread
src\Core\Actions.cs       running actions, hold action
src\Core\Toggle.cs        on/off switches
src\Core\Obs.cs           obs-websocket v5 client
src\Core\Reset.cs         Reset
src\Core\GameWatch.cs     fullscreen game detection + warnings
src\Core\AudioCtl.cs      mic mute, device switching, app mute
src\Core\WindowPlacer.cs  window rules
src\Core\Pad.cs           pad programming over HID
src\Ui\                   main window, popup, icons, theme
src\Tests.cs              self-tests
keycaps\                  printable keycap label generator
```

</details>

## Development

Needs the .NET 10 SDK.

```powershell
.\build.ps1          # → dist\PadDeck.exe   (quit PadDeck from the tray first)
.\build.ps1 -Test    # also runs the self-tests (130)
```

## License

[MIT](LICENSE) © Felix Wang
