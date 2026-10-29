# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build

Open `HorribleSubsDownloader.sln` in Visual Studio and build with **Ctrl+Shift+B**, or from the command line:

```
msbuild HorribleSubsDownloader\HorribleSubsDownloader.csproj /p:Configuration=Debug
```

Output (`Kizuna.exe`) lands in `HorribleSubsDownloader\bin\Debug\`. There are no automated tests in this project.

The `Transmission.API.RPC.dll` reference has a machine-specific HintPath; the build resolves it from a copy in `bin\Debug`.

## Architecture

A WPF desktop application (.NET 4.8, app name **Kizuna**) that bridges a custom anime-tracking backend with the Transmission torrent client. UI is a MahApps.Metro `MetroWindow` with a navigation bar, several views and an event log.

**Main files (`HorribleSubsDownloader\`):**
- `MainWindow.xaml` / `.xaml.cs` — main UI and most application logic (grids, timers, tray, window controls, library/duplicate views)
- `BackendApiClient.cs` — form-encoded POST client for the backend API
- `TransmissionService.cs` — Transmission RPC wrapper
- `SettingsWindow.xaml(.cs)`, `AppSettings.cs`, `SettingsStorage.cs` — settings UI and persistence
- `LocalizationManager.cs` — UI strings (en, de, fr, es, ja)
- `LibraryScanner.cs`, `LibraryRule.cs`, `LibraryFileCache.cs` — Library Manager
- `DuplicateFinder.cs`, `DuplicateHashCache.cs` and related `Duplicate*` files — Duplicate Finder
- `TrayIconManager.cs` — system tray

**Data flow:**
1. On startup and on a timer, unrecognized and recognized files are fetched from the backend and shown in two grids.
2. **Unrecognized grid** — files the backend has no rule for; "Set Rule" opens the rule-management page in the browser.
3. **Download grid** — files matched to a rule; "Download" / "Download All" add the torrent to Transmission (magnets, or `.torrent` files saved to the configured download path first, with a magnet fallback from the hash) and then tell the backend via `mark-downloaded`.
4. Finished torrents (100 %, seeding) are removed from Transmission and the backend is notified.
5. A short timer polls `rule-count`; when it changes, both grids refresh.

**Backend API:** all endpoints are POST with `username` + `apitoken` form fields. Used: `unrecognized-files`, `recognized-files`, `rule-count`, `mark-downloaded`, `delete-entry`, `franchise-name`, `season-name`.

## Settings (no hardcoded values)

Nothing configurable is hardcoded. All settings (API base URL, username, API token, torrent download path, Transmission URL, language, batch delay, library/duplicate folders, window layout, …) live in `AppSettings` and are persisted DPAPI-encrypted to `%AppData%\Kizuna\settings.dat` via `SettingsStorage`. They are edited in the Settings window. Always write changes through `SettingsStorage.Update` rather than load-modify-save with a fresh object. The only built-in default is the Transmission URL `http://127.0.0.1:9091/transmission/rpc`.

Caches (`duplicate_hashes.json`, `library_cache.json`) are stored next to it in `%AppData%\Kizuna\`.

## Key dependencies

- `MahApps.Metro 1.6.5` — window chrome (the title bar uses custom minimize/maximize/close buttons using Segoe MDL2 icons)
- `Newtonsoft.Json 12.0.3` — JSON handling
- `Transmission.API.RPC` — Transmission RPC client (DLL referenced directly, not from NuGet)
