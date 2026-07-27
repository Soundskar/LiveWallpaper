# LiveWallpaper

A lightweight native Windows app that plays a **video** (MP4, WebM, MKV, MOV, AVI, WMV) or **GIF** as your desktop wallpaper — rendered behind your desktop icons, controlled from the system tray.

Built with **C# / .NET 8 (WPF)** and an embedded **VLC** engine (LibVLCSharp) for universal codec support, hardware-accelerated playback, and seamless looping.

## Features

- Play any video or animated GIF as a live desktop background
- Runs behind desktop icons via the `Progman → WorkerW` technique (same approach as Wallpaper Engine / Lively)
- System-tray controls: choose file, pause/play, start-with-Windows, exit
- Remembers your last wallpaper across restarts
- Hardware-accelerated, low footprint (~120 MB RAM including the VLC engine)

## Requirements

- Windows 10 / 11
- [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0) (to build)

## Build

```bash
dotnet build LiveWallpaper.csproj -c Release
```

## Run

```
bin\Release\net8.0-windows\LiveWallpaper.exe
```

On first launch a file picker appears — choose a video or GIF. After that, use the **system-tray icon** (bottom-right of the taskbar) to change the wallpaper, pause/play, toggle start-with-Windows, or exit.

## How it works

- `MainWindow` — a borderless, full-screen window hosting a VLC `VideoView`.
- `WorkerW.cs` — Win32 interop that spawns the `WorkerW` window behind the desktop icons and reparents the wallpaper window into it.
- `Settings.cs` — persists the last file and manages the Windows startup registry entry.
- `App.xaml.cs` — application bootstrap and system-tray menu.

## Roadmap

- Multi-monitor support (v1 targets the primary monitor)
- GUI settings window and a wallpaper library / playlist
- Auto-pause when a fullscreen app or game is running

## License

MIT
