# Dynamic Island Music (Stage 1)

Frameless, transparent, always-on-top music widget. Stage 1 is visuals only:
three fake tracks, no media backend, no tray, no real playback.

`src-tauri/icons/icon.ico` is a deliberately plain placeholder. Tauri requires
an icon file to generate the Windows resource even in dev mode; replace it with
a real app icon before shipping.

## Install once

1. **Rust** (stable, MSVC toolchain) from <https://rustup.rs/>. Pick the
   `x86_64-pc-windows-msvc` host and the default Visual Studio C++ build tools
   workload when prompted.
2. **Visual Studio 2022 Build Tools** with **Desktop development with C++**
   (MSVC, Windows SDK, C++ CMake tools). Rust's MSVC linker needs this.
3. **WebView2 Runtime** from
   <https://developer.microsoft.com/microsoft-edge/webview2/>. Windows 11 and
   recent Windows 10 builds usually have it already.
4. **Node.js 20 or newer** from <https://nodejs.org/>.

Check the install:

```powershell
rustc --version
npm --version
```

## Create the project (if you want the scaffold)

The files in this folder already are the finished Stage 1 project. If you want
to see the generator first, run this in an empty folder:

```powershell
npm create tauri-app@latest
```

Pick: project name anything, **vanilla**, **JavaScript**, package manager npm.
Then replace the generated files with the ones from this folder.

## Run

```powershell
cd DynamicIslandTauri
npm install
npm run tauri dev
```

The first Rust build takes a few minutes. Tauri's built-in dev server serves
`web/` and reloads the frontend on save.

## What you should see

- A small black pill centered near the top of the primary monitor.
- Hover: it grows into a card after 80 ms. Leaving reverses after 200 ms.
- Click the pill to toggle the expanded state immediately.
- Previous / play-pause / next work against the fake track list.
- Play-pause also stops and starts the visualizer bars.
- Long titles scroll once when expanded.
- Click-through: the transparent area around the pill passes clicks through.
- `F2` hides and shows the island. `F3` toggles the FPS counter. The window may
  need one click first because it opens unfocused by design.
- Devtools helpers: `window.__islandDebug.hideIsland()`, `.showIsland()`,
  `.changeTrack(1)`, `.setExpanded(true)`.

## Layout

```text
DynamicIslandTauri/
  package.json                  npm script + Tauri CLI
  README.md
  web/
    index.html                  pill structure and control buttons
    style.css                   morph, stagger, reduced-motion rules
    main.js                     fake tracks, springs, rect reporting, rAF
  src-tauri/
    Cargo.toml
    build.rs
    tauri.conf.json             fixed transparent window
    icons/
      icon.ico                  plain placeholder required by Windows resource
    src/
      main.rs                   window placement + cursor polling
      media.rs                  empty, reserved for Stage 2
```

## Notes for Stage 2

`main.rs` already has `set_pill_rect` and `set_island_visible` commands, so the
frontend will not change much. `media.rs` will own the Windows media session and
emit track, playback and timeline events. Bundle and tray are disabled in
`tauri.conf.json` until then.
