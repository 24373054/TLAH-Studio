# TLAH Studio 4.15.0 — Immersive Aquarium

## Release Intent

The aquarium gives the Windows workspace an authored visual signature without turning decoration into an always-on rendering tax. The 4.15 implementation prioritizes depth, material response, organic motion, and graceful stillness. It remains optional, keyboard accessible, lifecycle-safe, and fully local at runtime.

## Visual Architecture

The control uses a fixed 276×128 design space and renders five cooperating layers:

| Layer | Implementation | Purpose |
|---|---|---|
| Frame and glass | WinUI borders and gradients | Metallic enclosure, waterline, reflection, focus, and hover response |
| Depth plate | Packaged `aquarium-depth-plate-v2.png` | Rock, substrate, distant foliage, and deep-water atmosphere |
| Vegetation and light | XAML paths plus Composition transforms | Separate rear/front plants and slow organic caustics |
| Fish | Composition containers and clipped sprites | Curved travel, depth, direction changes, and selective tail articulation |
| Water volume | Composition shape fields | Distributed bubbles and suspended particulate drift |

The depth plate was created with OpenAI Imagegen as project-owned source artwork, refined for a clear center and useful side depth, and committed as an opaque local asset. Image generation is not called by the installed application. Fish sprites are decoded once per asset and shared across actors.

## Motion Model

- Deterministic seeded routes keep movement stable across runs and place direction changes outside the visible glass.
- Position and direction are compositor keyframes; there is no per-frame managed-code loop.
- Articulated fish split one shared surface into body and tail clips. Small or distant fish can remain rigid to reduce channels.
- Bubble geometry is duplicated vertically inside one field for a seamless rise loop. Particle, plant, and caustic motion are grouped rather than animated object by object.
- `SizeChanged` scales the compositor root. It does not rebuild fish or restart timelines.

Auto is the recommended quality profile. Eco reduces actors and ambient work, Balanced preserves the intended composition, and High adds density for capable systems. The selected profile and explicit pause state are stored in the normal local preference store.

## Accessibility and Lifecycle Contract

The aquarium behaves as one decorative toggle, not as dozens of focusable elements. It supports pointer, Enter, and Space activation; exposes a state-aware automation name and help text; and draws a visible keyboard focus ring.

Animation is allowed only when all gates are true:

1. The control is loaded and the expanded sidebar is active.
2. The application window is active.
3. The user has not paused the aquarium.
4. Windows animations are enabled.
5. High Contrast and Energy Saver are both off.

If any gate closes, timelines stop and a deliberately composed poster frame remains. Load/unload pairs every Windows, accessibility, power, and window subscription and releases composition/image resources. This prevents duplicate channels and stale callbacks after collapse, navigation, theme changes, or reopen.

## Performance Principles

- Use one scaled composition root and shared GPU-decoded image surfaces.
- Prefer a few shape fields and keyframe tracks to many XAML elements or timers.
- Keep generated texture dimensions bounded and package only release assets.
- Pause all ambient work outside the active expanded sidebar.
- Treat quality profiles as density budgets; artistic hierarchy remains consistent at every level.

Performance is a constraint, not the artistic target. A lower profile may remove secondary density, but it must preserve the frame, depth plate, hero fish, water volume, and readable static state.

## Web Search Reliability Included in 4.15

This release also closes the visible zero-result failure path reported after 4.14. Structured fallbacks now select GDELT and language-matched Wikipedia according to query intent and recency, fall through after one retryable failure, apply a local provider cooldown, and preserve provider/license attribution. Cross-source research continues to distinguish strong coverage, partial evidence, conflicts, and no independently fetched evidence.

## Acceptance Checklist

- [ ] Visual hierarchy remains convincing in Light and Dark themes at 100%, 125%, 150%, and 200% scale.
- [ ] Auto, Eco, Balanced, and High visibly change scene density without layout shifts.
- [ ] Pause state and quality survive navigation and application restart.
- [ ] Collapse/reopen and deactivate/reactivate never duplicate actors or restart from a corrupted state.
- [ ] Reduced motion, High Contrast, and Energy Saver show the poster and consume no continuous animation work.
- [ ] Keyboard focus, toggle state, tooltip, and screen-reader text describe pause/resume behavior.
- [ ] Search fallback, provider pacing, language selection, recency exclusion, and attribution regression tests pass.
- [ ] Full Release CI and the packaged-app smoke test pass before tagging.
