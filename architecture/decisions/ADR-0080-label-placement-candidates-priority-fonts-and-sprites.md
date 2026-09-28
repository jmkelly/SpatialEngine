---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
---

# ADR-0080: Label placement is candidate-based, priority-ordered, and draws from a multi-face font registry

## Context

ADR-0049 made labels deterministic: HarfBuzz shaping over one embedded face,
an embedded SVG sprite registry, and a greedy first-fit collision pass in
style-document order with feature id then envelope centre as the sort. That is
reproducible, and it is the floor rather than the whole job. Three gaps showed
up as soon as a style asked for anything MapLibre styles actually use:

1. **One position per feature.** The pass had a single candidate — the feature's
   point, or its envelope centre for everything else — so a label that lost a
   collision simply vanished rather than trying the offsets MapLibre offers
   around the point, and a line feature could only ever be labelled at its
   midpoint. `symbol-placement: line` was a typed rejection.
2. **No priority.** Order was document order, so the style could not say that
   a city's label matters more than a hamlet's, and could not say it across
   layers: a symbol layer is compiled and drawn in the same order it appears in.
3. **One face, one sprite.** `text-font` accepted a fixed alias list and
   rejected anything else, so a style asking for bold got a typed error rather
   than a label; the sprite registry had exactly one entry; and there were no
   text transforms at all.

The expression dialect (ADR-0074) now in the style compiler is what makes the
style side of this possible to express, and the work sits **within** ADR-0049:
the embedded, digest-pinned assets, the byte-identical rerun and the
feature-order independence are all kept. Nothing here reopens them.

## Decision

1. **Placement generates ordered candidates per feature.** A point feature
   offers its own position and then the four anchor offsets at
   `2 × (text-size + text-padding)`; a polygon or line offers its envelope
   centre and the same offsets. Under `symbol-placement: line` a line
   geometry offers a candidate every `symbol-spacing` pixels along the
   projected line, each carrying the local line direction, with a single
   candidate at the midpoint when the line is shorter than the spacing, and
   then the same candidates again in reverse so a crowded start does not hide
   a free label further along. The greedy first-fit takes the first candidate
   that places anything, and drops the feature when none does. A candidate is
   a `(x, y, degrees)` triple: the label's box, the icon's box and both draws
   happen in that frame, which is what makes a line label run along its line
   and a `text-offset` read perpendicular to it.

2. **The first-fit order is a total order over a style priority.**
   `symbol-sort-key` first, then the style's document order, then the existing
   feature-id / envelope-centre tie-break inside a layer. Placement is now a
   **pass of its own** over all symbol layers before anything is painted, so a
   low sort key in a later layer still wins a collision against an earlier one,
   while the pixels keep the document order and the compositing a reader
   expects. `symbol-allow-overlap` (inheriting the per-component
   `text-`/`icon-allow-overlap` flags when unset) and `symbol-ignore-placement`
   bypass the collision test; ignored labels neither block nor are blocked.

3. **A font face registry over the embedded Noto Sans family.** The bundle
   grows to the four faces of Noto Sans 2.003 from the same pinned upstream tag
   (Regular, Bold, Italic, Bold Italic), each with its own recorded SHA-256
   that the loader verifies before it builds the typeface, exactly as ADR-0049
   required of the single face. `text-font` no longer validates: each name in
   the list walks a documented chain — the family and requested weight/style if
   bundled, else the nearest bundled weight (CSS font matching: heavier first,
   then lighter) with the upright face when no slanted one exists, else the
   next name — and a family the bundle has never heard of falls to the default
   face. **A font name is never a typed error.** A style that names a family
   the engine does not carry renders in the default face instead of failing the
   render, which is the behaviour MapLibre itself has.

4. **The sprite registry is a marker set.** `default-marker` plus circle,
   square, diamond, triangle and ring, all the same 24×24 template and
   stroke. The registry mechanism, the embedded-only rule and the typed error
   for an unknown name are unchanged.

5. **Text transforms are an evaluation step on the shaped string.**
   `text-transform` (none/uppercase/lowercase) is applied before shaping;
   `text-letter-spacing` becomes extra advance between the runs the shaper's
   HarfBuzz cluster map produced, so shaping stays HarfBuzz's on both the
   tracked and untracked paths; `text-line-height` is the baseline-to-baseline
   advance between the lines of a multi-line `text-field`; and `text-rotate`
   is a fixed rotation of the label about its anchor.

6. **The contracts do not move.** Every new type — `FontFace`,
   `FontFaceRegistry`, `FontSession`, `SymbolCandidate`, `ShapedLabel`,
   `PlacedSymbol`, `SymbolPlacementEngine` — is internal to
   `Spatial.Rendering.Skia`, as is the extra `SymbolOptions` surface. The
   documented dialect is style JSON over the existing render routes, so
   `Spatial.Contracts`, the SDK and the host take no new type and no new
   package.

## Consequences

- A label that loses its first choice now usually draws at the next one, so
  dense maps label more features at the same viewport. That is a real change
  in output, and it is the point of the bead; it is confined to styles whose
  labels actually collide, and a style with no collisions renders exactly the
  pixels it did before (the committed `symbols.png` golden is unchanged by
  this work).
- Placement is a separate pass, so a render shapes each label twice — once to
  decide, once to draw. The pass is O(features × candidates) with the candidate
  list generated lazily per feature, and the added cost is measured in the
  render benchmarks rather than asserted here.
- The assembly grows by three font files (~1 MB total for the family) and five
  small SVGs. All are pinned, so the size cost is a known constant.
- Priority spanning layers means a style's symbol layers no longer place
  strictly top-down. The *pixels* still composite top-down; only the choice of
  which label wins a collision follows the sort key. A style that relied on
  document order winning collisions must set `symbol-sort-key` explicitly.
- `text-font` is now permissive. A style naming a family the bundle lacks
  renders in Noto Sans Regular instead of failing, which trades a loud failure
  for a silent substitution; the rendered face is visible in the output, and
  the alternative (rejecting) is what this decision removes on purpose.
- The documented subset still stops short of MapLibre's full symbol model:
  curved placement, variable placement along a line, sprite sheets and
  expressions in symbol *layout* are not claimed and remain typed rejections.

## Alternatives

- **Keep one candidate and let losers disappear** — simplest and the status
  quo; it is the behaviour that makes a crowded map look empty, and it is what
  MapLibre's candidate list exists to avoid. Rejected.
- **Sort inside each layer only** — no extra pass, but then a sort key cannot
  express "this layer's labels matter more than that layer's", which is the
  point of a style-level priority. Rejected.
- **One pass that both places and draws, in priority order** — the cheapest
  code, but it changes the compositing order of symbol layers against the fill
  and line layers between them, so a symbol could be painted under a polygon
  the style put above it. Rejected in favour of a placement pass plus an
  ordered draw.
- **Synthesise bold by emboldening the Regular face** — no extra asset; but
  synthetic bold is not Noto Bold, and the family is already a dependency. The
  real face is pinned and free, so synthesis is unnecessary.
- **Validate `text-font` against the bundle and reject the rest** — a loud
  failure is easier to debug than a substituted face. Rejected: the failure is
  a whole render 400-ing on a font name, and MapLibre styles in the wild name
  families no engine bundles. The chain is documented, and the digests keep the
  faces themselves honest.
- **Bring in a font-matching package** (SixLabors.Fonts and friends) — solves
  matching, at the cost of reading host fonts, which ADR-0049 ruled out and
  which would break the golden images. Rejected.

## Implementation status

Implemented in `Spatial.Rendering.Skia`: candidate generation
(`SymbolCandidates`), the priority-ordered placement pass
(`SymbolPlacementEngine`) and its draw (`SkiaSymbolRasterizer`); the
`FontFaceRegistry`/`FontSession` chain over four digest-pinned Noto Sans faces;
the six-entry marker sprite set; and the four text transforms on the shaped
string. The style dialect gains `symbol-placement`, `symbol-spacing`,
`symbol-sort-key`, `symbol-allow-overlap`, `symbol-ignore-placement`,
`text-transform`, `text-letter-spacing`, `text-line-height` and `text-rotate`;
`text-font` stops rejecting unknown families. Covered by
`FontFaceRegistryTests`, `SymbolPlacementTests`, `SpriteRegistryTests`,
`SymbolReaderTests`, the render facade's line-placement order-independence
test, and the two committed goldens.
