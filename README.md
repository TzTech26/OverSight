# Oversight

A low-overhead Windows screen magnifier built for gamers, powered by the native
Windows Magnification API. Runs in the system tray, activated by rebindable global
hotkeys, and optionally restricted to specific applications.

---

## Features

| Feature | Details |
|---|---|
| **Fullscreen live zoom** | Entire display magnified around the cursor via `MagSetFullscreenTransform`. ~60 fps. No overlay window. |
| **Scroll-to-zoom** | Mouse wheel adjusts zoom level while active (captured via `WH_MOUSE_LL`). |
| **Hotkey + / −** | Configurable zoom-in / zoom-out key bindings. |
| **Process whitelist** | Hotkeys only fire when a whitelisted app is in focus; otherwise keys pass through normally. |
| **Auto clicker** | Repeats any key or mouse button on a bindable start/stop key. Adjustable interval (with optional random jitter), single/double click, fixed screen point, and stop conditions: until stopped, for a duration, or for a click count. Toggle or hold activation. Per-game via Game Profiles; off everywhere by default. |
| **Rebindable hotkeys** | Every action is configurable via a "click to capture" UI. Modifier combos supported. |
| **Start on boot** | Optional registry Run key (HKCU). |
| **System tray** | Icon changes colour when zoom is active. Context menu for toggle, settings, quit. |

---

## Install (end users)

Download and run **`Oversight-Setup-<version>.exe`**. It's a self-contained
installer — no .NET or other prerequisites needed, and it installs per-user with
no admin/UAC prompt. The latest tested installer lives in
[releases/verified/](releases/verified/).

> The first run may show a Windows SmartScreen prompt because the .exe isn't
> code-signed. Click **More info → Run anyway**. See
> [releases/README.md](releases/README.md) for details.

---

## Requirements

- **Windows 11** (build 22000+), 64-bit
  - Build 22621+ (22H2) required for Mica backdrop on the Settings window
  - Windows 10 is explicitly not supported (`SupportedOSPlatformVersion = 10.0.22000.0`)
- The installer **bundles** the .NET 8 runtime, so end users need nothing extra.
  To build/run from source you need the .NET 8 SDK
  ([download](https://dotnet.microsoft.com/download/dotnet/8.0)).

---

## Build from source

```powershell
# Requires .NET 8 SDK  https://dotnet.microsoft.com/download/dotnet/8.0
dotnet build Oversight.sln -c Release
```

Build output is redirected off the network share (see the `BaseOutputPath` in
`Oversight/Oversight.csproj`); the exe lands at:

```
%LOCALAPPDATA%\Oversight_build\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\Oversight.exe
```

To publish a self-contained build (bundles the .NET runtime):

```powershell
dotnet publish Oversight/Oversight.csproj -c Release -r win-x64 --self-contained true
```

---

## Installer & releases

Building a distributable installer and managing versioned releases is handled by
the scripts in [installer/](installer/), which output to [releases/](releases/):

```powershell
# Build the installer (-> releases\beta\)
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
# After testing, promote it (-> releases\verified\, the file you publish)
powershell -ExecutionPolicy Bypass -File installer\promote-release.ps1 -Version <ver>
```

See [installer/README.md](installer/README.md) for what each script does and
[releases/README.md](releases/README.md) for the channel layout, version manifest,
and website-publishing notes.

---

## Default hotkeys

| Action | Default |
|---|---|
| Toggle zoom on/off | **F12** |
| Zoom in | **Ctrl + =** |
| Zoom out | **Ctrl + −** |
| Show / hide crosshair | **Ctrl + Alt + X** |
| Start / stop auto-clicker | **Ctrl + Alt + C** |

All bindings are configurable in **Settings → Hotkeys**.

---

## Usage

1. **Launch** — A magnifier icon appears in the system tray.
2. **Press F12** — The entire screen magnifies 2× around your cursor.
3. **Scroll wheel** — Increase or decrease zoom while active.
4. **Press F12 again** (or Escape concept: re-press toggle) — Exits zoom.
5. **Tray icon** → **Settings** — Configure everything.

### Adding a game to the whitelist

1. Open **Settings → Whitelist**.
2. Click **Browse .exe…** and select the game's executable, or type the process
   name (e.g. `cs2`, `Overwatch`, `RocketLeague`) and click **Add**.
3. Enable **"Only activate for apps on the Whitelist"** in **General**.
4. Click **OK**.

Hotkeys will now pass through in all other applications.

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
4. Set the **Start / stop auto-clicker** key on the **Hotkeys** tab (saved with the slot).
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
%APPDATA%\Oversight\settings.json
```

The file is human-readable JSON. You can edit it manually while the app is closed.

---

## Module overview

```
Oversight/
├── Native/NativeMethods.cs       P/Invoke for Magnification, user32, hooks, SendInput
├── Models/                       AppSettings, HotkeyBinding, zoom/crosshair/auto-clicker slots
├── Services/
│   ├── ConfigStore.cs            JSON load/save, per-feature save slots (schema v5)
│   ├── HotkeyManager.cs          RegisterHotKey on a message-only HwndSource
│   ├── MagnifierService.cs       60-fps Dispatcher timer, fullscreen magnification
│   ├── CrosshairService.cs       Layered overlay, per-app show/hide
│   ├── AutoClickerService.cs     SendInput click loop, stop conditions, safety
│   ├── ProcessWatcher.cs         WinEvent hook (EVENT_SYSTEM_FOREGROUND)
│   └── StartupManager.cs         Registry HKCU Run key
├── Tray/TrayManager.cs           WinForms NotifyIcon, context menu, icon generation
└── UI/
    ├── SettingsWindow.xaml/.cs   Tabbed settings dialog
    └── HotkeyCaptureBox.cs       Custom TextBox that captures key combos
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
