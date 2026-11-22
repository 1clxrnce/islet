# islet

A static starter template with a full screen hero, a right-facing character portrait with blue goggles, concentric blue ripple waves, and a Persona inspired menu inside a soft organic panel. The menu uses loose Shrikhand lettering, a royal selection ribbon, and wave shapes borrowed from a 70s psychedelic game menu while staying blue only. Four real pages ship with it: `index.html`, `about.html`, `projects.html`, `skills.html`, and `contact.html`. No build step and no dependencies. Move the mouse and the portrait's head and body answer at slightly different rates. If your browser reports reduced motion, use the small MOTION button in the footer to opt into the movement for this session. Hover or focus a link and a short lock-on marker runs from the goggles to that option.

## Preview

Open `index.html` directly, or serve the folder:

```bash
npx.cmd serve .
```

Google Fonts supplies Shrikhand, so the display face needs an internet connection. Everything else is local.

## Files

| File | Purpose |
| --- | --- |
| `index.html` | Main menu screen |
| `about.html`, `projects.html`, `skills.html`, `contact.html` | Destination pages |
| `styles.css` | Palette, layout, menu states, wave shapes, page wipe |
| `transitions.js` | Arrow key focus, page wipe links, frame-smoothed pointer parallax, motion preview toggle, lock-on marker |
| `assets/hero.png` | Single-image fallback: cropped, mirrored and transparently padded supplied portrait |
| `assets/hero-head.png`, `assets/hero-body.png` | Soft-masked layers of the same portrait, stacked for parallax |
| `DESIGN.md` | Direction, color reasons, contrast, motion notes |

The `icons/` folder is leftover from an earlier template and is not used here.

## Customize

- Change the four labels and `href` values inside `.menu__list` on every page.
- Add your own content to each `.page-note` or replace it with real sections when you are ready.
- Change the blues in `:root` in `styles.css`. Keep the contrast values listed in `DESIGN.md` if you edit them.
- Replace `assets/hero.png` with your own transparent portrait. Keep the `width` and `height` attributes on the `<img>` updated to match. If you move the goggles, update the source point in `gogglePoint()` in `transitions.js`.
- To keep layered motion, split the new portrait into two images on the same canvas as `hero.png` and update the two `hero__image` sources. The head layer should carry the goggles so the marker stays attached. Without JavaScript, `hero.png` is shown instead.
- Edit the wave paths in the inline `<svg>` on each page if you want different ribbon shapes.

## Navigation and keyboard use

- Tab moves through the menu in visual order.
- Up and Down arrows move focus between links. Left and Right do the same. Home goes to the first link, End to the last.
- Enter follows a link.
- The skip link jumps straight to the menu.
- The current page is marked with `aria-current="page"` and a visible highlight shape.

## Motion

The wave group settles once on arrival and the menu links unfurl in a short stagger. The body and head layers lift into place in sequence, with the head drifting slightly against the body. Internal links sweep three curved blue panels across the screen before the next page loads, then move out on arrival. With a mouse, the head leads the body toward the pointer at a slightly different rate. Pointer values are interpolated once per animation frame, so continuous mouse movement stays smooth instead of restarting a CSS transition on every event. A short dashed lock-on marker runs from the goggles to the edge of the menu option you hover or focus. On a destination page the marker rests on the current page. Under `prefers-reduced-motion: reduce`, the arrival motion, unfurl, layer parallax, panel movement, and pointer turn are removed by default. Wipes become short fades and the marker jumps to its target. The MOTION footer button is an explicit opt-in that restores full motion for the current session. Touch screens do not get the pointer parallax. Nothing loops, pulses, flashes, strobes, or blinks.

## Design notes

See `DESIGN.md` for the full direction. The short version: navy, royal, and sky blue plus white, one wavy italic display face, one large portrait, one soft organic panel with a royal print offset, one flowing selection ribbon, concentric ripple waves, one lock-on marker, and quick curved page wipes.
