# islet

Dynamic Island style media controller for Windows. It shows and controls the media that is already playing in Edge, Spotify, or another app. It is not a music player.

The widget sits at the top center of a monitor and reads the current session through `GlobalSystemMediaTransportControlsSessionManager`, so it follows whatever Windows reports as playing.

## Features

- Mini mode by default: a small pill with tiny album art, a scrolling title, and a thin progress line along the bottom.
- The window is always on top, borderless, hidden from the taskbar and Alt+Tab, and never takes focus (`WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`, `HWND_TOPMOST`).
- Hovering the pill fades it out and flips on `WS_EX_TRANSPARENT`, so clicks land on the browser tab or app behind it. Leaving fades it back and restores hit testing.
- Hover is detected by polling `GetCursorPos` against `GetWindowRect` every 16 ms. A click-through window receives no mouse events, so WPF mouse handlers are not used for this. Opacity eases toward its target each tick.
- Hold Alt (or Ctrl) to expand into the full controller with click-through off, so every button works. Release to shrink back to mini. Ctrl+Alt+E toggles a sticky expanded state.
- Previous, play/pause, and next resolve a fresh session on every press, check the result, and fall back to normal Windows media keys when an app refuses. They stay enabled whenever a session exists.
- Elapsed and total time plus a live progress bar. The session timeline is re-read every 250 ms and interpolated locally between updates so the bar glides. Live streams show `LIVE`.
- Drag the progress bar to seek, with a time tooltip.
- Album art retries a few times when a browser publishes it late, is cached per track, and shows a music-note placeholder when there is none. The accent color comes from the artwork.
- Volume button with a slider on hover, click to mute, and scroll over the pill to change real system volume. External volume changes are picked up every 300 ms.
- Pin a specific app as the media source when several are playing, or leave it on Automatic.
- Right-drag the pill to move it, or pick a monitor in Options.
- Long titles marquee. Channel labels like ` - Topic` are stripped, and `Artist - Title` is split when the artist is missing.
- Fullscreen apps hide the widget until you use the top-center hover zone.
- System tray icon with Settings, Start with Windows, and Exit.

All animation is done with GPU-composited transforms and opacity (WPF's default render path), so the pill stays at 60 fps with little CPU work.

## Requirements

- Windows 10 version 19041 or newer, or Windows 11.
- .NET 8 SDK or Visual Studio 2022 with the .NET desktop development workload.

The project targets `net8.0-windows10.0.19041.0`. No NuGet packages are needed; WPF, WinForms, the Windows SDK media bindings, and the Core Audio COM interfaces all come with the target framework.

## Build and run

From the repository root in PowerShell:

```powershell
dotnet build DynamicIslandMusic\DynamicIslandMusic.csproj
.\DynamicIslandMusic\bin\Debug\net8.0-windows10.0.19041.0\DynamicIslandMusic.exe
```

Or run directly:

```powershell
cd DynamicIslandMusic\
dotnet run
```

Start playing something in a media app and move the cursor to the top center. Right-click the tray icon and choose Exit to close it.

## Controls

| Action | Result |
| --- | --- |
| Move cursor to top center | Reveals the mini pill |
| Hover the pill | Fades it out and makes it click-through |
| Move the cursor away | Fades it back in, then hides it after a delay |
| Hold Alt or Ctrl | Expands the controller with working buttons |
| Release Alt or Ctrl | Returns to mini mode |
| Ctrl+Alt+E | Toggles sticky expanded mode |
| Click the pill | Toggles expanded / mini when hover-dodge is off |
| Click the progress bar | Seeks to that position |
| Click the volume button | Mutes or unmutes |
| Hover the volume button | Expands it into a slider |
| Scroll over the pill | Changes system volume by 5 percent |
| Right-drag the pill | Moves it to a new position |
| Previous, play/pause, next | Sends the command to the active media session |
| Ctrl+Alt+Space | Play or pause |
| Ctrl+Alt+Right | Next track |
| Ctrl+Alt+Left | Previous track |

If no media is playing, the pill stays hidden. That is expected.

## Options

Open Settings from the tray icon, or click the gear in the expanded pill.

| Setting | Effect |
| --- | --- |
| Media source | Automatic follows the playing app. Pick a name to pin one app. |
| Monitor | Primary monitor, or a specific display. |
| Slide away when nothing is playing | Turns auto-hide on or off. |
| Dodge the cursor on hover | Turns the fade-and-click-through behavior on or off. |
| Ctrl + Alt + Space / Left / Right | Turns the global media hotkeys on or off. |
| Start with Windows | Adds or removes the registry run entry. |

## Project files

| File | Purpose |
| --- | --- |
| `DynamicIslandMusic/MediaService.cs` | Session selection, timeline, transport with media-key fallback, album art retry and cache, title cleanup |
| `DynamicIslandMusic/VolumeService.cs` | System volume and mute through Windows Core Audio |
| `DynamicIslandMusic/MainViewModel.cs` | Live clock, seek, volume and playback state, options fields |
| `DynamicIslandMusic/MainWindow.xaml` | Mini, peek, and expanded visuals, volume controls, typography |
| `DynamicIslandMusic/MainWindow.xaml.cs` | State machine, 16 ms cursor polling, click-through, hotkeys, low-level wheel hook, drag, animations, positioning |
| `DynamicIslandMusic/OptionsWindow.xaml` and `.cs` | Settings window wired to the tray and the gear button |
| `DynamicIslandMusic/App.xaml` and `App.xaml.cs` | Application startup and shutdown |
| `DynamicIslandMusic/TrayIcon.cs` | Tray menu and exit |
| `DynamicIslandMusic/app.manifest` | DPI awareness settings |

## Notes

- Some apps refuse seek requests or report a stale timeline. The widget follows what Windows gives it.
- `TryChangePlaybackPositionAsync` uses ticks. Some players ignore it.
- Fullscreen hiding checks the foreground window bounds. Borderless overlays with a visible edge may not trigger it.
- Keyboard-only operation is not provided because the widget uses `WS_EX_NOACTIVATE` and never steals focus. The global hotkeys work regardless of focus.
- The build emits one `WFAC010` warning about high DPI settings in `app.manifest`. It is left alone on purpose: the manual DPI math used for accurate hover hit testing depends on that setting.
- Build output under `bin/`, `obj/`, and similar folders is ignored by Git.
