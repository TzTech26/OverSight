# Oversight

A low-overhead screen magnifier, crosshair overlay and auto clicker built for gamers, for
Windows and Linux. On Windows it uses the native Magnification API. Runs in the system tray,
activated by rebindable global hotkeys, and optionally restricted to specific applications.

---

## Features

| Feature | Details |
|---|---|
| **Fullscreen live zoom** | Entire display magnified around the cursor via `MagSetFullscreenTransform`. ~60 fps. No overlay window. |
| **Scroll-to-zoom** | Mouse wheel adjusts zoom level while active (captured via `WH_MOUSE_LL`). |
| **Hotkey + / −** | Configurable zoom-in / zoom-out key bindings. |
| **Game Profiles** | Pick a zoom, crosshair and auto-clicker save slot per game; they switch automatically when that game is focused, with separate choices for everywhere else. |
| **Auto clicker** | Repeats any key or mouse button on a bindable start/stop key. Adjustable interval (with optional random jitter), single/double click, fixed screen point, and stop conditions: until stopped, for a duration, or for a click count. Toggle or hold activation. Per-game via Game Profiles; off everywhere by default. |
| **Rebindable hotkeys** | Every action is configurable via a "click to capture" UI. Modifier combos supported. |
| **Start on boot** | Optional registry Run key (HKCU). |
| **Self-updating** | Installs new GitHub releases on its own after checking their SHA-256. |
| **System tray** | Icon changes colour when zoom is active. Context menu for toggle, settings, quit. |

---

## Install

1. Download **`Oversight-win-x64.zip`** from the
   [latest release](https://github.com/TzTech26/OverSight/releases/latest).
2. Unzip `Oversight.exe` into a folder you own, for example `%LOCALAPPDATA%\Programs\Oversight`,
   and run it. It's a single self-contained exe: no .NET or installer needed, no admin prompt.

Oversight adds itself to the Start menu on first run. Turn on **Run Oversight when Windows
starts** in **Settings → General** to have it in the tray at sign-in.

> Releases are unsigned until [code signing](docs/signing.md) is set up, so the first run may show a SmartScreen
> prompt (**More info → Run anyway**), and with Smart App Control on, Windows may block a version outright. If it
> blocks an update, Oversight keeps the version you have.

### Linux

1. Download **`Oversight-linux-x64.AppImage`** (or `-arm64`) from the
   [latest release](https://github.com/TzTech26/OverSight/releases/latest).
2. `chmod +x Oversight-linux-x64.AppImage` and run it. It adds itself to the app menu; turn on
   **Run Oversight when you log in** in **Settings → General** to have it in the tray after login.

Settings, Game Profiles and save slots work as on Windows, and a `settings.json` copied from
Windows loads unchanged (`~/.config/Oversight/settings.json`).

- **X11:** everything works. **Wayland:** hotkeys, the crosshair and the auto clicker go through
  XWayland, so they work while an X11 window is focused, which includes most games through Steam
  and Proton, but not native Wayland apps.
- **Zoom** drives the desktop's own magnifier: GNOME (also Ubuntu, Unity, Budgie), Cinnamon and
  KDE Plasma. Your magnifier settings are put back when zoom turns off. On other desktops the zoom
  keys are left alone. Scroll-to-zoom and the zoom animations are Windows-only.
- **Tray:** needs a StatusNotifier tray (KDE, Cinnamon, XFCE; on GNOME the AppIndicator extension,
  which Ubuntu ships).

### Updates

Oversight checks the GitHub releases about once a day and, by default, installs new versions
on its own: it downloads the zip (or the AppImage on Linux), checks it against the release's
`SHA256SUMS.txt`, swaps the exe or AppImage in place and restarts in the tray. Both behaviours can be turned off in
**Settings → General → Updates**, which also has a **Check for updates** button.

---

## Requirements

- **Windows 11** (build 22000+), 64-bit
  - Build 22621+ (22H2) for the Mica backdrop on the Settings window
- **Linux**, x64 or arm64, with an X11 session or XWayland, and libXtst / libXi (installed on
  practically every desktop)
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

---

## Build from source

```powershell
dotnet build Oversight.sln -c Release
# The release exe (single file, bundles the .NET runtime):
dotnet publish Oversight/Oversight.csproj -c Release -r win-x64 -o out
```

A development build (with `Oversight.dll` beside the exe) never updates itself.

On Linux, `tools/build-appimage.sh linux-x64` (or `linux-arm64`, on a machine of that
architecture) builds `Oversight-linux-x64.AppImage`. `oversight --self-test` checks hotkeys,
the crosshair overlay, an XTest click, the focused app and settings.json against the current
X display; CI runs it under Xvfb.

### Releasing

Push a tag like `v2.3.0`. The [Build workflow](.github/workflows/build.yml) publishes the exe
with that version, signs it if [SignPath is set up](docs/signing.md), zips it, builds and self-tests the
Linux AppImages, writes `SHA256SUMS.txt` and creates the GitHub release that installed copies update
from. Pushes to `main` and pull requests build an unsigned zip and the AppImages as workflow
artifacts for testing.

---

## Working on the UI

Set `OVERSIGHT_PROFILE` to run an isolated copy beside a normal install. It uses its own
config folder (`%APPDATA%\Oversight-<profile>`, `~/.config/Oversight-<profile>` on Linux), its own single-instance lock, registers no
global hotkeys, never self-updates, and opens Settings straight away:

```powershell
$env:OVERSIGHT_PROFILE = "dev"
dotnet run --project Oversight
```

Shared control styles live in `Oversight/Themes/Controls.xaml`; colours are in
`Themes/Dark.xaml` / `Themes/Light.xaml` (swapped live with the Windows theme). Unhandled
errors are written to `error.log` in the config folder. `tools/make-icon.ps1` regenerates
the app icon.

---

## Default hotkeys

| Action | Default |
|---|---|
| Toggle zoom on/off | **F8** |
| Zoom in | **Ctrl + =** |
| Zoom out | **Ctrl + −** |
| Show / hide crosshair | **Ctrl + Alt + X** |
| Start / stop auto-clicker | **Ctrl + Alt + C** |
| Open settings | **Ctrl + Alt + Z** |

Each feature's keybinds are on its own page (Zoom, Crosshair, Auto Clicker) and saved with that slot; **Open settings** is in **General**.

---

## Usage

1. **Launch** — A magnifier icon appears in the system tray.
2. **Press F8** — The entire screen magnifies 2× around your cursor.
3. **Scroll wheel** — Increase or decrease zoom while active.
4. **Press F8 again** — Exits zoom.
5. **Tray icon** → **Settings** — Configure everything.

### Per-game setup

1. Open **Settings → Game Profiles**.
2. Click **Add running app…** and pick the game.
3. Choose its zoom, crosshair and auto-clicker slots (or *(off)*). Changes apply right away.
4. Under **Everywhere else**, choose what runs outside your listed games.

---

## Auto clicker

The auto clicker repeats a key or mouse button while it runs. Like zoom and the
crosshair it is **save-slot based and assigned per game** in **Game Profiles**, and
it is **off everywhere by default** — nothing clicks until you both assign a slot to
an app and press its start/stop key while that app is focused.

1. Open **Settings → Auto Clicker**.
2. Set the **key / button to repeat** (any key or mouse button), the **interval**,
   and **stop after** (until stopped / a duration / a click count). Optionally enable
   **random jitter**, a **fixed screen point**, and a **safety countdown**.
3. Choose **Toggle** (press to start / stop) or **Hold** (clicks only while held).
4. Set the **Start / stop auto-clicker** key under **Activation & safety** on the same page (saved with the slot).
5. In **Game Profiles**, pick this auto-clicker slot for the games you want it in.

> **Safety.** A run stops the moment the assigned app loses focus, when you quit
> Oversight, and after any crash. Use the optional safety countdown to make a runaway
> clicker easy to catch. As with zoom, injected input may not register in
> exclusive-fullscreen games — use **Borderless / Windowed** mode.

---

## Fullscreen vs. borderless windowed

> **Exclusive fullscreen games (D3D9/D3D11/D3D12 fullscreen)** typically bypass
> the Desktop Window Manager (DWM), which means the Magnification API's
> fullscreen transform has no effect.
>
> **Use borderless windowed mode** in your game's graphics settings for zoom
> to work correctly. Most modern games support this natively; for those that
> don't, tools like Borderless Gaming can force it.

---

## Configuration file

Settings are persisted to:

```
%APPDATA%\Oversight\settings.json        Windows
~/.config/Oversight/settings.json       Linux
```

The file is human-readable JSON. You can edit it manually while the app is closed.

---

## Module overview

```
Oversight.Core/                   Shared by both apps (net10.0)
├── Models/                       AppSettings, HotkeyBinding, zoom/crosshair/auto-clicker slots, VirtualKeys
└── Services/
    ├── ConfigStore.cs            JSON load/save, per-feature save slots (schema v5)
    ├── AppProfile.cs             OVERSIGHT_PROFILE isolated copies
    └── Updater.cs                GitHub Releases self-update (SHA-256 checked), zip or AppImage
Oversight/                        Windows (WPF)
├── Native/NativeMethods.cs       P/Invoke for Magnification, user32, hooks, SendInput
├── Services/
│   ├── HotkeyManager.cs          RegisterHotKey on a message-only HwndSource
│   ├── MagnifierService.cs       60-fps Dispatcher timer, fullscreen magnification
│   ├── CrosshairService.cs       Layered overlay, per-app show/hide
│   ├── AutoClickerService.cs     SendInput click loop, stop conditions, safety
│   ├── ProcessWatcher.cs         WinEvent hook (EVENT_SYSTEM_FOREGROUND)
│   └── StartupManager.cs         Registry HKCU Run key, Start menu shortcut
├── Tray/TrayManager.cs           WinForms NotifyIcon, context menu, icon generation
└── UI/
    ├── SettingsWindow.xaml/.cs   Tabbed settings dialog
    └── HotkeyCaptureBox.cs       Custom TextBox that captures key combos
Oversight.Linux/                  Linux (Avalonia)
├── Platform/
│   ├── X11.cs                    Xlib, XTest, XShape, XInput2 P/Invoke
│   ├── HotkeyManager.cs          XGrabKey on the root window, own connection and thread
│   ├── KeyMap.cs                 Virtual keys to keysyms, key and button polling
│   └── Desktop.cs                Autostart, app menu entry, notifications, single instance
├── Services/                     Magnifier (gsettings / KWin), crosshair window, auto clicker, focus watcher
├── Ui/                           Tray, Settings window, key boxes and dialogs
└── SelfTest.cs                   --self-test
```

---

## Performance notes

- **Fullscreen mode** calls `MagSetFullscreenTransform` once per frame (~16 ms
  DispatcherTimer). The transform is applied by DWM, not in the application's
  render path, so CPU/GPU overhead is minimal.
- The **scroll-wheel hook** (`WH_MOUSE_LL`) is installed only while zoom is active
  and removed on deactivation to avoid any latency impact during normal play.
- All magnifier calls are on the WPF Dispatcher (UI thread); no background threads
  are involved in the hot path.

---

## License

[MIT](LICENSE)
