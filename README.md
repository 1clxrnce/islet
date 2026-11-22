# islet

Dynamic Island style media controller for Windows. It shows and controls the media that is already playing in Edge, Spotify, or another app. It is not a music player.

The widget appears at the top center of the primary monitor. It reads the current session through `GlobalSystemMediaTransportControlsSessionManager`, so it follows whatever Windows reports as playing.

## Features

- Three states: compact, peek, and expanded.
- Hover the top center to reveal the pill. Move away and it slides back up after a short delay.
- Click the pill to expand it. Click again to return to peek.
- Previous, play/pause, and next buttons call the real media session. Buttons dim when the source does not support them.
- Drag the progress bar to seek, with a time tooltip while dragging.
- Elapsed and total time come from the session timeline and advance locally between media events.
- Live streams hide the seek bar and show `LIVE` instead of a duration.
- Album art drives the accent color used by the glow, equalizer, and progress bar.
- Long titles marquee. Japanese, Chinese, and Korean titles use Yu Gothic UI or Meiryo UI as fallbacks.
- The window is always on top, borderless, hidden from the taskbar and Alt+Tab, and click-through outside the pill.
- Multiple sessions are handled by picking the one that is playing, then the last active one.
- Fullscreen apps hide the widget until you use the top-center hover zone.

## Requirements

- Windows 10 version 19041 or newer, or Windows 11.
- .NET 8 SDK or Visual Studio 2022 with the .NET desktop development workload.

The project targets `net8.0-windows10.0.19041.0`.

## Run it

From PowerShell:

```powershell
cd DynamicIslandMusic
dotnet run
```

Or build and launch the exe:

```powershell
dotnet build
& ".\bin\Debug\net8.0-windows10.0.19041.0\DynamicIslandMusic.exe"
```

Start playing something in a media app. The pill appears at the top center. Right-click the tray icon and choose Exit to close it.

## Controls

| Action | Result |
| --- | --- |
| Move cursor to top center | Reveals the pill in peek state |
| Hover the pill | Keeps it visible |
| Click the pill | Toggles expanded state |
| Click the progress bar | Seeks to that position |
| Previous, play/pause, next | Sends the command to the active media session |
| Move cursor away | Returns to compact, then slides out of view |

If no media is playing, the pill stays hidden. That is expected.

## Setting

`MainViewModel.AutoHide` in `DynamicIslandMusic/MainViewModel.cs` controls the slide-away behavior. Set it to `false` to keep the pill parked at the top and expand only on hover.

## Project files

| File | Purpose |
| --- | --- |
| `DynamicIslandMusic/MediaService.cs` | Session selection, timeline data, transport commands, album art and accent extraction |
| `DynamicIslandMusic/MainViewModel.cs` | Live clock, seek target, time formatting, playback state, `AutoHide` |
| `DynamicIslandMusic/MainWindow.xaml` | Compact, peek, and expanded visuals, storyboards, icons, typography |
| `DynamicIslandMusic/MainWindow.xaml.cs` | State machine, cursor polling, animations, click-through, positioning, seek dragging |
| `DynamicIslandMusic/App.xaml` and `App.xaml.cs` | Application startup and shutdown |
| `DynamicIslandMusic/TrayIcon.cs` | Tray menu and exit |
| `DynamicIslandMusic/app.manifest` | DPI awareness settings |

## Notes

- Some apps refuse seek requests or report a stale timeline. The widget follows what Windows gives it.
- Fullscreen hiding checks the foreground window bounds. Borderless overlays with a visible edge may not trigger it.
- Build output under `bin/`, `obj/`, and similar folders is ignored by Git.
