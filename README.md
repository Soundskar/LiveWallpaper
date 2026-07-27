# LiveWallpaper

A lightweight native Windows app that plays a **video** (MP4, WebM, MKV, MOV, AVI, WMV) or **GIF** as your desktop wallpaper — rendered behind your desktop icons, controlled from the system tray.

Built with **C# / .NET 8 (WPF)** and an embedded **VLC** engine (LibVLCSharp) for universal codec support, hardware-accelerated playback, and seamless looping.

## Features

- Play any video or animated GIF as a live desktop background
- Runs behind desktop icons via the `Progman → WorkerW` technique (same approach as Wallpaper Engine / Lively)
- System-tray controls: choose file, pause/play, start-with-Windows, exit
- Remembers your last wallpaper across restarts
- Hardware-accelerated, low footprint (~120 MB RAM including the VLC engine)

### Desktop pet 🐾

An optional vector "desktop pet" (a little blob-cat named Pixel) that lives **strictly on the desktop layer** — it never covers your apps, and empty space around it is click-through so your desktop right-click still works.

**Lively behavior**
- Wanders, idles, sits, sleeps, blinks, and jumps on its own
- Eyes follow your cursor; greets you by time of day; sleeps at night
- **Drag it** around — it falls with gravity and squishes on landing

**Useful utilities**
- **Health reminders** (toggleable): 20‑20‑20 eye rest, hourly hydration, and stretch/posture nudges
- **Focus timer** (Pomodoro): 25 or 50 min — the pet settles while you focus and celebrates when done
- **Battery awareness**: warns you when the battery runs low

**Care & personality**
- Happiness, hunger, and energy stats that slowly change and **persist between sessions** (decays sensibly while the app is closed)
- Grows from baby to adult over its first week; shows a level in the menu
- Right-click menu: Feed 🍪 · Play 🎾 · Come here 👋 · Do a spin 🌀 · Sleep 😴 · Focus timer · Party mode ✨ · Rename · Hide

**Easter eggs:** double-click for a happy hop + hearts, click it rapidly to make it dizzy-spin, and a rainbow "party mode".

**Light & efficient:** a single adaptive frame loop (fast only while moving, slow while idle), reused brushes with no per-frame allocations, and it fully **suspends while a fullscreen app or game is focused** so it never causes lag.

Enable it from the tray menu → **Show desktop pet 🐾**.

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
- `Settings.cs` — persists the last file, pet options, and the Windows startup registry entry.
- `App.xaml.cs` — application bootstrap and system-tray menu.
- `PetWindow.xaml` / `PetWindow.xaml.cs` — the optional desktop pet: a transparent always-on-top window with a code-drawn vector character, a behavior state machine, physics-based dragging, and easter eggs.

## Roadmap

- Multi-monitor support (v1 targets the primary monitor)
- GUI settings window and a wallpaper library / playlist
- Auto-pause when a fullscreen app or game is running

## License

MIT
