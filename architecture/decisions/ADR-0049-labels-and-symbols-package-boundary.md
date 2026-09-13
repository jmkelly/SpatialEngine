---
status: accepted
date: 2026-09-16
deciders: maintainer + agent
---

# ADR-0049: Labels and symbols use bundled HarfBuzz shaping and an embedded SVG sprite registry

## Context

ADR-0044 chose SkiaSharp for vector rasterization and explicitly left labels,
symbols and collision out of the decision (R6 of
`architecture/rendering-implementation-plan.md`). R6 now needs three things
the current `Spatial.Rendering.Skia` project cannot do with SkiaSharp alone:

1. **Text shaping** — complex scripts (and correct advance/kerning for Latin)
   need HarfBuzz. SkiaSharp ships the binding as the separate
   `SkiaSharp.HarfBuzz` package; the research note flags it as the intended
   companion (`research/rendering/README.md` §Why SkiaSharp).
2. **SVG icons** — the MapLibre `icon-image`/sprite vocabulary addresses named
   images, and the project has no image pipeline for them. `Svg.Skia` is the
   MIT, first-party binding that turns SVG into a Skia `SKPicture`.
3. **A deterministic font** — a renderer that falls back to the host's
   installed fonts produces different pixels on every machine and makes
   golden images meaningless. `AGENTS.md` and the plan's testing strategy
   both require **no system-font dependence**.

These are new packages in a platform project. `architecture/distilled/README.md`
(§How to change the architecture) requires an ADR and an architecture-test
allowlist entry *before* the guard accepts them — the same deliberate step
ADR-0044 took for SkiaSharp and NetVips. The three standing walls apply:
ADR-0005 (renderer types never cross a contract), ADR-0033 (implementations
depend on contracts, never on each other) and ADR-0040 (the new code must
clear the metrics/CRAP/coverage gates).

SkiaSharp itself is not enough for either verb: `SKTypeface.FromFamilyName`
reads the host font manager (the forbidden system-font path), and the base
package has no SVG reader. The two companion packages keep the Skia surface
inside the owning assembly.

## Decision

1. **Two packages, one project.** `Spatial.Rendering.Skia` adds
   `SkiaSharp.HarfBuzz` (pinned with SkiaSharp at `4.152.0`) and `Svg.Skia`
   (`5.2.3`). Both are MIT. They are added to the architecture allowlist for
   that project only; neither the SDK nor any other implementation may take
   them.

2. **Text is shaped with HarfBuzz over an embedded font.** A single
   `SKTypeface` is created from an embedded assembly resource with
   `SKTypeface.FromStream`; `SKTypeface.FromFamilyName`/the host font manager
   is never called. The bundled face is **Noto Sans Regular 2.003**
   (`Resources/Fonts/NotoSans-Regular.ttf`, SHA-256
   `dac8e68fe43fca59d522fa5f763322cfb4a919c28957656c58e7836d915307d0`),
   licensed **SIL Open Font License 1.1**; the licence text ships beside the
   font and is embedded too. The asset is version-pinned: changing it means
   changing the recorded version and hash and re-approving the golden images.

3. **Sprite references resolve to embedded SVGs only.** `icon-image`
   addresses a `SpriteRegistry` built from the embedded
   `Resources/Sprites/*.svg` resources plus the bundled default icon. The
   registry accepts SVG bytes/names, never a URL: a style cannot name an
   arbitrary remote image (the SSRF rule from ADR-0044 §11). An unknown icon
   name is a typed `invalid.arguments`, not a silent skip. `Svg.Skia` parses
   each icon once into an `SKPicture` (thread-safe to draw), so parallel tile
   renders share one picture per icon.

4. **Placement is a deterministic greedy pass.** Symbol candidates are
   collated in style-document order; within a layer features are sorted by a
   canonical key (feature id, then source coordinates) so store page order
   cannot change the output. Each candidate is measured (HarfBuzz advances
   plus font metrics), oriented by `text-anchor` and `text-offset`, padded by
   `text-padding`, and first-fit against the already-placed boxes. Ties are
   broken by that same order; `*-allow-overlap` bypasses the test. No random,
   time or hash-order input enters the pass.

5. **The style dialect grows a documented `symbol` subset, and rejects the
   rest.** Supported layout: `visibility`, `text-field`, `text-font`,
   `text-size`, `text-anchor`, `text-offset`, `text-padding`,
   `text-allow-overlap`, `icon-image`, `icon-size`, `icon-allow-overlap`.
   Supported paint: `text-color`, `text-halo-color`, `text-halo-width`,
   `text-opacity`. Any other layer type, layout key or paint key is a typed
   `invalid.arguments` naming it, exactly as the other layer types already
   behave. `symbol-placement: point` is the only placement mode; `line` is
   rejected rather than approximated.

6. **The contracts do not move.** Labels and icons are compiled into the
   internal `DrawPlan`; `Spatial.PluginSdk` gains no HarfBuzz, Svg.Skia or
   Skia type. The host routes (`POST /api/render`,
   `POST /api/render/tiles/...`) already carry the style document as JSON, so
   they render labels without a wire change.

## Consequences

- The renderer gains its two largest R6 capabilities without violating
  ADR-0005: the shaped glyphs, `SKPicture` icons and collision boxes all stay
  inside `Spatial.Rendering.Skia`.
- The container/assembly grows by the HarfBuzz native library (~1 MB) and a
  ~340 KB font; both are pinned, so the size cost is a known constant, not a
  floating one. Native interop keeps the host JIT (ADR-0012/0021).
- Three licence notices ship with the binary: SkiaSharp/SkiaSharp.HarfBuzz
  (MIT), Svg.Skia and its dependencies (MIT) and Noto Sans (OFL-1.1). The
  OFL notice travels as an embedded resource next to the font.
- Golden images become reproducible because the font, the SVG parser and the
  Skia native library are all pinned; the strict byte comparison remains a
  CI-Linux assertion, with a tolerance on other platforms.
- The documented subset deliberately stops short of MapLibre's full symbol
  model: line placement, expressions in `text-field`, sprite sheets with
  pixel ratios and `text-transform` are not claimed. Each earned a typed
  rejection instead of a silent approximation.
- The `Svg.Skia` dependency set (HarfBuzzSharp native assets, `Svg.Model`,
  `ShimSkiaSharp`) is transitive; only `HarfBuzzSharp.NativeAssets.Linux` is
  pinned directly so the managed and native HarfBuzz versions cannot drift.

## Alternatives

- **`SKTypeface.FromFamilyName` / system fonts** — no extra package, but it
  makes output host-dependent and breaks the golden strategy. Rejected.
- **`SkiaSharp` text with no shaping** — `SKFont.MeasureText`/`SKCanvas.DrawText`
  advances by simple glyph metrics; breaks for scripts that need reordering
  or ligatures and is not what R6 asks for. Rejected.
- **A sprite sheet + `SKBitmap.Decode`** — avoids `Svg.Skia` and is what
  MapLibre does in the browser, but the engine has no sprite-sheet pipeline
  and the task names SVG icons. Rejected for R6 (a future sprite-sheet
  optimization is additive).
- **A second font per weight/style** — better typography, more assets and
  more pins; deferred until a style needs it. The single Regular face is the
  documented bundle.
- **Putting the font behind `IWebHostEnvironment`/config** — configurable
  fonts reintroduce host dependence and a validation surface; the embedded
  pin is simpler and deterministic. Rejected.

## Implementation status

Implemented with R6: `symbol` layers (`text-field`, `text-font`, `text-size`,
`text-anchor`, `text-offset`, `text-padding`, `*-allow-overlap`,
`icon-image`, `icon-size`; `text-color`, `text-halo-color`, `text-halo-width`,
`text-opacity`), HarfBuzz shaping over the embedded Noto Sans face, the
deterministic collision pass, and the embedded SVG sprite registry with the
bundled default marker. Architecture allowlists updated in the same change.
Not claimed: line placement, expressions, sprite sheets, gradients on text.
