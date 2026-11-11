# JWall

A lightweight live wallpaper engine for Windows 10 and 11 using native Windows desktop hooks and WebView2.

## Features

- 1.2 MB download
- Near-zero CPU usage (automatically freezes animations when other apps are focused)
- Full desktop mouse interaction
- Runs websites, live streams, and local HTML5/WebGL projects

## Quick Start

1. Download `JWall.exe` from the Releases tab and place it in any empty folder you want.
2. Run `JWall.exe`. If Windows asks you to install the .NET Desktop Runtime, click **Yes** to let Microsoft install it.
3. The app will launch in your system tray and automatically create a `wallpapers` folder with two preloaded examples ready to use.

## Adding Your Own Wallpapers

Every subfolder inside `wallpapers` becomes an option in your tray menu.

### Local HTML5 or WebGL
- Create a folder inside `wallpapers` (e.g. `wallpapers\Matrix`).
- Place your web files inside.
- If your folder has multiple HTML files, name the main one `index.html` (if there is only one HTML file, it is loaded automatically).

### Websites or Live Streams
- Create a folder inside `wallpapers` (e.g. `wallpapers\SpaceStream`).
- Inside that folder, create a `.txt` file containing the `https://` link on the first line.
- *Tip:* You can also drop a `.txt` link directly into `wallpapers` without making a subfolder.

## Default Wallpapers Used

- [Live Earth](https://github.com/Pickle776/Live-Earth) — Real-time 3D Earth render with live cloud sync.
- [NASA Live Stream](https://www.youtube.com/@NASA/live) — 24/7 space stream.

## Building from Source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ../dist
```
