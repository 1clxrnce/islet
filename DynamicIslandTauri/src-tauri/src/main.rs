#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod media;

use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

use tauri::{AppHandle, Manager, PhysicalPosition, RunEvent, WebviewWindow};

// The pill reports its visual rectangle in physical window pixels on every frame.
// The cursor poller compares that rectangle with the OS cursor, also in physical pixels.
#[derive(Default)]
struct ClickState {
    left: AtomicI32,
    top: AtomicI32,
    width: AtomicI32,
    height: AtomicI32,
    visible: AtomicBool,
    ignoring: AtomicBool,
    running: AtomicBool,
}

impl ClickState {
    fn new() -> Self {
        Self {
            visible: AtomicBool::new(true),
            running: AtomicBool::new(true),
            ..Default::default()
        }
    }

    fn contains(&self, x: f64, y: f64) -> bool {
        let left = self.left.load(Ordering::Relaxed) as f64;
        let top = self.top.load(Ordering::Relaxed) as f64;
        let right = left + self.width.load(Ordering::Relaxed) as f64;
        let bottom = top + self.height.load(Ordering::Relaxed) as f64;
        x >= left && x <= right && y >= top && y <= bottom
    }
}

// Only touch the window flag when the answer changes. Calling it every 16 ms
// makes the webview flicker on some WebView2 builds.
fn set_ignore(window: &WebviewWindow, state: &ClickState, ignore: bool) {
    if state.ignoring.load(Ordering::Relaxed) == ignore {
        return;
    }
    if window.set_ignore_cursor_events(ignore).is_ok() {
        state.ignoring.store(ignore, Ordering::Relaxed);
    }
}

fn spawn_cursor_poller(app: tauri::AppHandle, state: Arc<ClickState>) {
    thread::spawn(move || {
        let mut outside_since: Option<Instant> = None;

        while state.running.load(Ordering::Relaxed) {
            thread::sleep(Duration::from_millis(16));

            let Some(window) = app.get_webview_window("main") else {
                continue;
            };

            if !state.visible.load(Ordering::Relaxed) {
                outside_since = None;
                set_ignore(&window, &state, true);
                continue;
            }

            // cursor_position() and inner_position() are both desktop-relative
            // physical pixels, so subtracting them gives window-relative pixels.
            let (Ok(cursor), Ok(origin)) = (window.cursor_position(), window.inner_position()) else {
                continue;
            };
            let inside = state.contains(
                cursor.x - origin.x as f64,
                cursor.y - origin.y as f64,
            );

            if inside {
                outside_since = None;
                set_ignore(&window, &state, false);
            } else if state.ignoring.load(Ordering::Relaxed) {
                outside_since = None;
            } else {
                // Give the webview time to see pointerleave before it goes
                // transparent, otherwise the 200 ms leave intent never runs.
                let since = *outside_since.get_or_insert_with(Instant::now);
                if since.elapsed() >= Duration::from_millis(240) {
                    set_ignore(&window, &state, true);
                    outside_since = None;
                }
            }
        }
    });
}

// The native window never moves or resizes after this. Pixel maths keeps the
// pill centred on the primary monitor with a small top gap on any DPI scale.
fn position_at_top_center(window: &WebviewWindow) {
    let Ok(Some(monitor)) = window.primary_monitor() else {
        return;
    };
    let Ok(size) = window.outer_size() else {
        return;
    };

    let work = monitor.work_area();
    let x = work.position.x + (work.size.width as i32 - size.width as i32) / 2;
    let y = work.position.y + (10.0 * monitor.scale_factor()).round() as i32;
    let _ = window.set_position(PhysicalPosition::new(x, y));
}

#[tauri::command]
fn set_pill_rect(
    left: f64,
    top: f64,
    width: f64,
    height: f64,
    state: tauri::State<'_, Arc<ClickState>>,
) {
    state.left.store(left.round() as i32, Ordering::Relaxed);
    state.top.store(top.round() as i32, Ordering::Relaxed);
    state.width.store(width.round() as i32, Ordering::Relaxed);
    state.height.store(height.round() as i32, Ordering::Relaxed);
}

#[tauri::command]
fn set_island_visible(visible: bool, state: tauri::State<'_, Arc<ClickState>>) {
    state.visible.store(visible, Ordering::Relaxed);
}

fn main() {
    let app = tauri::Builder::default()
        .manage(Arc::new(ClickState::new()))
        .invoke_handler(tauri::generate_handler![set_pill_rect, set_island_visible])
        .setup(|app| {
            if let Some(window) = app.get_webview_window("main") {
                position_at_top_center(&window);
                let _ = window.set_ignore_cursor_events(true);

                if let Some(state) = app.try_state::<Arc<ClickState>>() {
                    state.ignoring.store(true, Ordering::Relaxed);
                    spawn_cursor_poller(app.handle().clone(), state.inner().clone());
                }
            }
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("failed to build Dynamic Island Music");

    app.run(|app: &AppHandle, event| {
        if let RunEvent::Exit = event {
            if let Some(state) = app.try_state::<Arc<ClickState>>() {
                state.running.store(false, Ordering::Relaxed);
            }
        }
    });
}
