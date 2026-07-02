# TrayStart

Automatically minimize any application to the system tray when it starts — whether or not the app natively supports it.

## How it works

TrayStart sits in the tray and listens for new top-level windows using an out-of-context WinEvent hook (`EVENT_OBJECT_SHOW`). This is fully event-driven: zero CPU while idle, no polling. When a watched program shows a window shortly after launching, TrayStart hides the window and puts a tray icon in its place (using the program's own icon).

- **Left-click** a trayed icon to restore the window.
- **Right-click** for Restore / Close window.
- Windows opened later from the app's own UI are left alone (only windows shown within a grace period after process start are hidden).
- All hidden windows are restored when TrayStart exits — nothing gets stranded invisible.

## Install

Download `TrayStart-win-Setup.exe` from the [latest release](https://github.com/larcobeats/TrayStart/releases/latest) and run it.

TrayStart checks for updates once on launch and via the **Check for updates** button. There is no background updater.

## Usage

1. Open TrayStart (double-click the tray icon for settings).
2. Add programs with **Add program…** (browse to an .exe) or **Add running window…** (pick from open windows).
3. Optionally enable **Start TrayStart when Windows starts**.

Closing the settings window keeps TrayStart running in the tray. Exit via the tray icon's right-click menu.

### Limitations

- Windows of elevated (admin) processes can't be hidden by a non-elevated TrayStart (Windows UIPI). TrayStart detects this and leaves them alone.
- UWP/Store apps hosted by `ApplicationFrameHost.exe` are not yet supported.

## Building from source

Requires the .NET 8 SDK.

```
dotnet build TrayStart.sln
dotnet run --project src/TrayStart
```

Update checks are disabled in dev builds (Velopack only updates installed copies).

## Releasing

Releases are built by GitHub Actions. Bump and tag:

```
git tag v0.2.0
git push origin v0.2.0
```

The workflow publishes the app, packs it with [Velopack](https://velopack.io), and uploads the installer + update packages to a GitHub Release. Installed copies pick it up on their next launch or manual check.

## Architecture

```
src/TrayStart/
├── Program.cs               Entry point: Velopack hooks, single-instance mutex
├── App.xaml(.cs)            Composition root, on-launch update check, lifetime
├── Interop/NativeMethods.cs Win32 P/Invoke surface
├── Models/                  WatchedApp, AppSettings (settings.json in %APPDATA%\TrayStart)
├── Services/
│   ├── WindowWatcher.cs     WinEvent hook — detects new windows of watched apps
│   ├── TrayMinimizer.cs     Hide window ↔ tray icon lifecycle, restore-all safety net
│   ├── SettingsService.cs   Load/save settings
│   ├── StartupService.cs    HKCU Run key (start at login)
│   ├── UpdateService.cs     Velopack update checks (launch + manual only)
│   └── AppTrayIcon.cs       TrayStart's own tray icon + menu
├── ViewModels/              MVVM viewmodels (CommunityToolkit.Mvvm)
└── Views/                   Settings window, running-window picker
```

New features generally mean: a new service under `Services/`, wired up in `App.xaml.cs`, surfaced through a viewmodel.
