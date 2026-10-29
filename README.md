# Kizuna

A Windows desktop app (WPF, .NET Framework 4.8) that connects a custom anime-tracking backend with the [Transmission](https://transmissionbt.com/) torrent client.

Kizuna fetches the episodes that match your download rules, adds them to Transmission automatically or on demand, and reports finished downloads back to the backend. It also cleans up completed torrents and refreshes itself whenever your rules change.

## Features

- **Episode grids** – recognized files ready to download and unrecognized files that still need a rule, with search and sort preservation
- **Downloads** – one-click or batch downloads with a configurable delay, automatic retry, conflict handling and undo
- **Transmission integration** – adds magnets or `.torrent` files, shows torrent progress, removes finished torrents and notifies the backend
- **Library Manager** – rule-based scanning, naming templates and moving of files into your anime library
- **Duplicate Finder** – staged size → quick → full hashing with a cache, keep/delete resolution and batch cleanup
- **System tray** – minimize-to-tray, autostart and optional notifications
- **Modern dark UI** – built on MahApps.Metro, localized in English, German, French, Spanish and Japanese
- **Local, encrypted settings** – stored with Windows DPAPI; no credentials are hardcoded

## Requirements

- Windows with .NET Framework 4.8
- A running Transmission daemon with RPC enabled (default `http://127.0.0.1:9091/transmission/rpc`)
- Access to a compatible tracking backend (API base URL, username and API token)

## Build

Open `HorribleSubsDownloader.sln` in Visual Studio 2022 and build, or:

```
msbuild HorribleSubsDownloader\HorribleSubsDownloader.csproj /p:Configuration=Release
```

Output is `HorribleSubsDownloader\bin\Release\Kizuna.exe`. NuGet packages are restored automatically.

`Transmission.API.RPC.dll` is not available via NuGet. Place it in the build output folder (or fix the `HintPath` in the `.csproj`) before building.

## Configuration

On first start, open **Settings** and enter:

| Setting | Description |
|---|---|
| API base URL | Endpoint of your tracking backend, e.g. `https://example.com/api/client/` |
| Username / API token | Credentials for the backend |
| Torrent download path | Where `.torrent` files are saved before being added |
| Transmission URL | RPC URL of the Transmission daemon |

Settings are saved to `%AppData%\Kizuna\settings.dat`, encrypted for the current Windows user. Caches live next to it in `%AppData%\Kizuna\`.

## Built with

C#, WPF, [MahApps.Metro](https://mahapps.com/), [Newtonsoft.Json](https://www.newtonsoft.com/json), Transmission.API.RPC
