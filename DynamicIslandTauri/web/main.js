// Stage 1: all track data is fake. No media backend is called anywhere here.

const island = document.getElementById('island');
const pill = document.getElementById('pill');
const shape = document.getElementById('shape');
const compactArt = document.getElementById('compactArt');
const artA = document.getElementById('artA');
const artB = document.getElementById('artB');
const titleEl = document.getElementById('title');
const artistEl = document.getElementById('artist');
const elapsedEl = document.getElementById('elapsed');
const totalEl = document.getElementById('total');
const progressFill = document.getElementById('progressFill');
const prevBtn = document.getElementById('prev');
const toggleBtn = document.getElementById('toggle');
const nextBtn = document.getElementById('next');
const fpsPanel = document.getElementById('fps');

// Tauri injects this into every page. Using it directly keeps the project
// free of a bundler and the @tauri-apps/api package.
const invoke = (command, args) =>
  window.__TAURI_INTERNALS__?.invoke(command, args) ?? Promise.resolve();
const call = (command, args) => invoke(command, args).catch(() => {});

function cover(from, to, cx, cy) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
    <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
      <stop stop-color="${from}"/><stop offset="1" stop-color="${to}"/>
    </linearGradient></defs>
    <rect width="256" height="256" fill="url(#g)"/>
    <circle cx="${cx}" cy="${cy}" r="82" fill="#ffffff" fill-opacity=".10"/>
    <circle cx="${cx}" cy="${cy}" r="46" fill="#000000" fill-opacity=".22"/>
    <rect y="196" width="256" height="60" fill="#000000" fill-opacity=".14"/>
  </svg>`;
  return `data:image/svg+xml,${encodeURIComponent(svg)}`;
}

const TRACKS = [
  {
    title: 'Nightshift',
    artist: 'Paper Lanterns',
    duration: 214,
    accent: '#c98b6b',
    art: cover('#7b4b3a', '#d9a37c', 168, 88)
  },
  {
    title: 'Slow Static',
    artist: 'Aster Vale',
    duration: 187,
    accent: '#7d9a86',
    art: cover('#34594a', '#9fbd9c', 92, 160)
  },
  {
    title: 'A Very Long Demo Title That Needs To Scroll Across The Card',
    artist: 'The Long Title Test',
    duration: 242,
    accent: '#9d8298',
    art: cover('#4a3550', '#b499b4', 140, 120)
  }
];

// Underdamped for expand (small overshoot), near-critical for collapse.
// linear() is supported by current WebView2; the CSS has bezier fallbacks.
function spring(stiffness, damping, duration = 0.55, steps = 48) {
  const w0 = Math.sqrt(stiffness);
  const zeta = damping / (2 * w0);
  const wd = w0 * Math.sqrt(Math.abs(1 - zeta * zeta));
  const stops = [];

  for (let i = 0; i <= steps; i += 1) {
    const t = (i / steps) * duration;
    let value;
    if (zeta < 1) {
      value = 1 - Math.exp(-zeta * w0 * t) *
        (Math.cos(wd * t) + (zeta * w0 / wd) * Math.sin(wd * t));
    } else {
      value = 1 - (1 + w0 * t) * Math.exp(-w0 * t);
    }
    stops.push(`${value.toFixed(4)} ${((i / steps) * 100).toFixed(1)}%`);
  }

  stops[0] = '0 0%';
  stops[steps] = '1 100%';
  return `linear(${stops.join(', ')})`;
}

if (window.CSS?.supports?.('transition-timing-function', 'linear(0, 1)')) {
  const root = document.documentElement.style;
  root.setProperty('--spring-expand', spring(230, 17, 0.55));
  root.setProperty('--spring-collapse', spring(170, 20, 0.5));
}

let index = 0;
let playing = true;
let basePosition = 42;
let timelineAt = performance.now();
let lastElapsed = '';
let frontArt = artA;
let expanded = false;
let enterTimer = 0;
let leaveTimer = 0;
let visible = false;
let hidingTimer = 0;
let frames = 0;
let fpsMark = performance.now();

const track = () => TRACKS[index];

function formatTime(seconds) {
  const whole = Math.max(0, Math.floor(seconds));
  return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
}

function positionAt(now) {
  const extra = playing ? (now - timelineAt) / 1000 : 0;
  return Math.min(track().duration, basePosition + extra);
}

function commitPosition() {
  const now = performance.now();
  basePosition = positionAt(now);
  timelineAt = now;
}

function setArt(url) {
  const incoming = frontArt === artA ? artB : artA;
  incoming.src = url;
  incoming.classList.add('front');
  frontArt.classList.remove('front');
  frontArt = incoming;
  compactArt.src = url;
}

function setupMarquee() {
  titleEl.classList.remove('marquee');
  titleEl.style.removeProperty('--marquee-shift');

  requestAnimationFrame(() => {
    const overflow = titleEl.scrollWidth - titleEl.parentElement.clientWidth;
    if (overflow > 4) {
      titleEl.style.setProperty('--marquee-shift', `${-overflow}px`);
      titleEl.classList.add('marquee');
    }
  });
}

function applyTrack() {
  const current = track();
  setArt(current.art);
  titleEl.textContent = current.title;
  artistEl.textContent = current.artist;
  totalEl.textContent = formatTime(current.duration);
  document.documentElement.style.setProperty('--accent', current.accent);
  setupMarquee();
}

function updateToggleIcon() {
  toggleBtn.innerHTML = playing
    ? '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 5h3.5v14H7zm6.5 0H17v14h-3.5z"/></svg>'
    : '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5v14l11-7z"/></svg>';
  toggleBtn.setAttribute('aria-label', playing ? 'Pause' : 'Play');
  toggleBtn.title = playing ? 'Pause' : 'Play';
}

function changeTrack(delta) {
  index = (index + delta + TRACKS.length) % TRACKS.length;
  basePosition = 0;
  timelineAt = performance.now();
  applyTrack();
}

function setExpanded(next) {
  if (next === expanded) return;
  expanded = next;
  pill.classList.toggle('is-expanded', next);
  document.getElementById('details').setAttribute('aria-hidden', String(!next));
  if (next) setupMarquee();
}

function showIsland() {
  if (visible) return;
  visible = true;
  clearTimeout(hidingTimer);
  island.classList.remove('hiding');
  island.classList.add('visible');
  call('set_island_visible', { visible: true });
}

function hideIsland() {
  if (!visible) return;
  visible = false;
  island.classList.remove('visible');
  island.classList.add('hiding');
  setExpanded(false);
  call('set_island_visible', { visible: false });
  hidingTimer = setTimeout(() => island.classList.remove('hiding'), 240);
}

function toggleFps() {
  fpsPanel.hidden = !fpsPanel.hidden;
  frames = 0;
  fpsMark = performance.now();
}

pill.addEventListener('pointerenter', () => {
  clearTimeout(leaveTimer);
  clearTimeout(enterTimer);
  enterTimer = setTimeout(() => setExpanded(true), 80);
});

pill.addEventListener('pointerleave', () => {
  clearTimeout(enterTimer);
  clearTimeout(leaveTimer);
  leaveTimer = setTimeout(() => setExpanded(false), 200);
});

pill.addEventListener('pointerdown', (event) => {
  if (event.target.closest('button')) return;
  clearTimeout(enterTimer);
  clearTimeout(leaveTimer);
  setExpanded(!expanded);
});

prevBtn.addEventListener('click', () => changeTrack(-1));
nextBtn.addEventListener('click', () => changeTrack(1));

toggleBtn.addEventListener('click', () => {
  commitPosition();
  playing = !playing;
  timelineAt = performance.now();
  island.classList.toggle('is-paused', !playing);
  updateToggleIcon();
});

window.addEventListener('keydown', (event) => {
  if (event.key === 'F3') {
    event.preventDefault();
    toggleFps();
  } else if (event.key === 'F2') {
    event.preventDefault();
    visible ? hideIsland() : showIsland();
  }
});

// Devtools hooks: window.__islandDebug.hideIsland() etc.
window.__islandDebug = { showIsland, hideIsland, toggleFps, changeTrack, setExpanded };

// Simulated Rust timeline event. Stage 2 replaces this with real position events.
setInterval(() => commitPosition(), 2500);

let lastRect = '';
function reportRect() {
  const rect = shape.getBoundingClientRect();
  const scale = window.devicePixelRatio || 1;
  const payload = {
    left: Math.round(rect.left * scale),
    top: Math.round(rect.top * scale),
    width: Math.round(rect.width * scale),
    height: Math.round(rect.height * scale)
  };
  const key = `${payload.left},${payload.top},${payload.width},${payload.height}`;
  if (key === lastRect) return;
  lastRect = key;
  call('set_pill_rect', payload);
}

function frame(now) {
  const current = track();
  const position = positionAt(now);
  progressFill.style.transform = `scaleX(${current.duration ? position / current.duration : 0})`;

  const label = formatTime(position);
  if (label !== lastElapsed) {
    elapsedEl.textContent = label;
    lastElapsed = label;
  }
  if (position >= current.duration && playing) changeTrack(1);

  reportRect();

  frames += 1;
  if (now - fpsMark >= 500) {
    if (!fpsPanel.hidden) {
      fpsPanel.textContent = `${Math.round((frames * 1000) / (now - fpsMark))} fps`;
    }
    frames = 0;
    fpsMark = now;
  }

  requestAnimationFrame(frame);
}

applyTrack();
updateToggleIcon();
timelineAt = performance.now();
requestAnimationFrame(frame);
setTimeout(showIsland, 120);
