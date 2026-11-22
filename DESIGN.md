# Design direction

Reading this as: a personal website starter with a Persona inspired game menu, in a blue-only psychedelic visual language, dial ENERGY 3 / RHYTHM 2 / MOTION 2.

This direction keeps the owner's blue-only palette and translates the supplied Persona 6 psychedelic menu reference into wavy shape, groovy lettering, and fluid but finite motion. The reference uses orange. Its colors are not copied. Its 70s poster energy is borrowed through ripple bands, loose menu rhythm, and soft organic shapes. The earlier angular HUD framing is retired because it reads as tactical rather than psychedelic.

Dials: ENERGY 3 because the type, portrait, and waves fill the screen at high contrast. RHYTHM 2 because the page is one deliberate composition with a few irregular breaks in the menu stack, not a uniform grid. MOTION 2 because there is a short arrival, a state change, and a page wipe, with no continuous choreography.

## Identity

One full-screen menu. A cropped, mirrored portrait sits on the left and faces a loose stack of four large white links on the right. A navy organic panel with a royal-blue offset sits behind the links. Concentric blue wave bands and thin ripple lines flow through the background. The selected link slides forward and a royal ribbon unfurls behind it. There is one focal point, the selected or hovered link. Every other element supports that moment.

## Palette

| Token | Value | Use |
| --- | --- | --- |
| Navy | `#061b4d` | Menu panel ground, text shadow, dark wave, template note |
| Royal | `#1749c7` | Panel offset, selection ribbon, mid wave, wipe panel |
| Sky | `#dce8ff` | Page ground, light wave, accent ripple lines, wipe panel |
| White | `#ffffff` | Menu lettering, selection tail, focus outline |

Measured contrast:

- White on navy: 16.51:1, passes for normal and large text.
- White on royal: 7.46:1, passes for normal and large text.
- Navy on sky: 13.39:1, passes for normal and large text.
- White on sky is not used for text.

Only these three blues plus white are used. Royal appears at reduced opacity in one background wave, which keeps the palette flat rather than adding a new hue. The goggles were recolored from orange to royal blue so the portrait stays inside the same system.

## Typography

- **Shrikhand** for the menu. Reason: its chunky, wavy, italic display lettering matches the reference's groovy voice and stays legible at large sizes.
- System sans for the short template note and footer. Reason: a neutral support face keeps attention on the display face without adding a second downloaded font.
- The menu fill stays white. A small navy and royal text shadow makes the lettering feel layered and printed rather than flat. The shadow is decoration only; the white fill always sits on the solid navy panel.

All menu links use uppercase as part of the requested game menu voice, not as decorative emphasis in prose.

## Layout

- Desktop: two unequal columns inside a full viewport hero. The portrait gets the larger share and is anchored bottom left. The menu starts on the right. Four hand-tuned horizontal offsets, 0px, 52px, 18px, and 68px, give the stack a loose 70s rhythm instead of a mechanical staircase.
- Tablet: the same two columns with a smaller display scale and tighter offsets so the menu never reaches the portrait.
- Mobile: one column. The portrait sits above a plain vertical menu with no hand-tuned offsets. All links stay well above a 44px tap target.
- The navy panel is a readability ground, not decoration. It keeps white text off the lighter waves at every width. Its soft asymmetric radii replace the old cut corners and give the panel a poster-like edge. A royal offset beneath it reads as a second printed layer.

## Image

The supplied PNG was cropped to its opaque bounds and mirrored horizontally so the figure faces right, toward the menu. The file keeps its transparency. The goggle pixels were shifted to royal blue while keeping their original lightness, so the highlights and shadows still read as glass. A transparent frame of 28px on each side and 48px top and bottom was added after cropping. Reason: the source touched its own bounds, which read as a cut head and feet. The frame restores air without changing the crop or adding new pixels. Near white pixels in the source are blended with `mix-blend-mode: multiply` over the sky ground, so no white box appears.

`assets/hero.png` remains the single-image fallback. For visitors with JavaScript, the same pixels are split into `assets/hero-body.png` and `assets/hero-head.png` with a soft elliptical mask centered near the goggles. The two layers share the original 896x1037 canvas and are stacked at exactly the same position, so the figure reads as one image at rest. The split is a parallax rig, not a redraw. The body keeps the multiply blend. The head's color is pre-multiplied against the sky and its feather alpha is boosted so it can composite normally over the body without leaving a dark seam or a thin gap where the masks overlap. Each layer is a decorative `<img>` with empty alt text, intrinsic dimensions, and high fetch priority.

## Motion

- Arrival: the wave group settles from a slight sideways offset over 800ms. The menu links unfurl in sequence over 520ms with a 70ms stagger. The body layer lifts into place over 820ms and the head layer follows over 960ms with a small opposing drift. All play once. Purpose: greet the visitor without a loop.
- Hover, focus, and selected states move a link 18px forward, scale it to 1.05, and tilt it by 1.5 degrees over 420ms with a soft overshoot. The royal ribbon unfurls from the left at the same time. Purpose: show the active option with a groovy, game-like response.
- Page changes use three curved blue panels that sweep across the screen over 420ms with a 60ms stagger. Entry uses the same panels moving out. Purpose: signal a page change without a long wait.
- On screens with a mouse, the two portrait layers answer the pointer at different rates. The body shifts up to 8px and turns 0.8 degrees around the feet. The head shifts up to 18px and turns 1.6 degrees around the neck, so the goggles lead the movement. Pointer values are interpolated once per animation frame instead of restarting a CSS transition on every mouse event, which removes the stutter when the pointer moves continuously. Purpose: the figure acknowledges where the visitor is pointing and gets closer to the reference video's separated motion without needing an animated source. It is disabled for touch input, for reduced motion, and when the pointer leaves the page.
- A short dashed lock-on marker leaves the goggles and stops just outside the active menu option. Purpose: it makes the selected option unmistakable and ties the choice back to the figure without drawing over the lettering. Its straight dashed line is the one geometric counterpoint to the waves. The index has no resting selection, so it appears only while a link is hovered or focused. Destination pages rest it on `aria-current="page"`. The source point comes from the goggle centroid in the padded image data, and the angle and length are recalculated on hover, focus, resize, and scroll.
- A small `MOTION` button sits in the footer. Reason: the in-app preview browser reports `prefers-reduced-motion: reduce`, so the site looks frozen there even though the parallax is built. The button is an explicit, opt-in preview that turns motion on for the current session only. It is a native button with `aria-pressed`, a visible on/off label, and a keyboard focus ring. It does not change the default for anyone who has asked their system for less motion.
- Under `prefers-reduced-motion: reduce`, the arrival animation, menu unfurl, layer parallax, pointer turn, and shape movement are removed unless the visitor has explicitly switched the preview on. Both portrait layers hold their shared resting position and read as the original single image. Page wipes become simple opacity fades of 200ms or less. The marker stays visible but snaps to the target. No element loops, pulses, strobes, or blinks.

## Small interface decisions

- The `MENU / SELECT` label is uppercase with wide tracking. Reason: it names the screen like a game menu header and gives the eye a quiet entry point above the large links. It is decorative and hidden from screen readers because the nav already has an accessible label.
- The diamond beside each link is a menu cursor, not a generic sparkle. Reason: it marks the selectable row and brightens only when that row is active.
- Thin ripple lines sit behind the panel. Reason: they carry the reference's flowing motion into the static background without adding another color or a repeating grid.
- The white tail on the selection ribbon keeps the pointed gesture from the game menu. Reason: it marks the chosen row's edge without drawing a line through the lettering.
- Soft panel radii and the royal offset repeat one printed-poster gesture. Reason: that shared shape gives the psychedelic direction its identity without adding a second typeface or a fourth blue.

## Navigation

ABOUT, PROJECTS, SKILLS, and CONTACT each open a real page. Every page repeats the menu and marks the current page with `aria-current="page"` plus a visible selection ribbon. Tab follows the visual order. Arrow keys, Home, and End move focus inside the menu. A skip link reaches the menu directly.

## Content honesty

Each destination is a template page with a short visible note telling the owner what to replace. There are no statistics, testimonials, client logos, fake profiles, or data views, so empty, loading, and error states do not apply.
