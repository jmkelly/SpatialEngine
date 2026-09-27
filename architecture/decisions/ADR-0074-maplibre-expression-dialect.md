---
status: accepted
date: 2026-09-27
deciders: maintainer + agent
---

# ADR-0074: The MapLibre style dialect is a compiled expression tree with a per-feature scope

## Context

ADR-0044 made the style document the shared vocabulary between the delivered
client and the server, and fixed the rule the renderer has followed ever
since: **an unsupported key is a typed `invalid.arguments`, never a silent
flattening**. ADR-0049 added the symbol subset on the same footing.

That discipline was applied to the *shape* of the dialect but not its
*depth*. `StyleCompiler` accepted a hand-rolled subset in which every paint
value is a constant and every filter is one of nine operators over literal
operands. A style authored by any real MapLibre tool — or by the workbench
composer as it grows — uses `interpolate`, `step`, `match`, `case`, `zoom`,
`geometry-type` and `get`, and was rejected outright with "expected a CSS
colour". The renderer already had every term such an expression needs: a zoom
level (`ZoomEstimator`), a per-feature attribute read (`IFeature`), a geometry
type and a per-feature identity. The infrastructure was there; the evaluator
was not.

Two constraints shape the decision.

- **Determinism is non-negotiable.** A committed golden render
  (`tests/fixtures/rendering/golden/symbols.png`) is compared byte-for-byte on
  CI, and label placement is a greedy first-fit that page order must not
  perturb (ADR-0049). An evaluator must not perturb a style that does not use
  one.
- **The hot path is real.** A tile render evaluates every feature of every
  layer; a naive evaluator re-runs the same `get` for every property of every
  layer that reads it.

## Decision

A style property compiles either to a **constant** or to a **compiled
expression node**, and the compiled style keeps both:

1. **The dialect is a closed node tree** (`StyleExpression`), compiled once
   per style document and interned: two properties that write the same
   expression text share one node. The served set is literals/`literal`,
   `get`/`has`, `zoom`, `id`, `geometry-type`, `let`/`var`, the comparisons,
   `all`/`any`/`!`, `in`, arithmetic, `concat`, `case`, `match`, `coalesce`,
   `step`, and `interpolate` over `linear` and `exponential`. An operator
   outside the set is a compile-time `invalid.arguments` naming it — the
   partial dialect is still a partial dialect, and it is a wide one, not a
   tiny complete one.

2. **It is typed, not coerced.** The compiler rejects an expression whose
   static type does not fit the property, naming both; a value only knowable
   per feature (`get`) is checked the same way at render time. A number is not
   a colour: `'circle-color'` cannot receive one. Where MapLibre would
   *coerce* — ramping a number into a colour — the engine refuses and says so,
   because a silently reinterpreted number is a wrong picture rather than a
   rejected style.

3. **A constant style compiles to exactly the recipe it always did.** A paint
   property with no expression resolves to itself and is drawn by the existing
   path, so the golden render is unchanged by construction, not by tolerance.

4. **Evaluation is per feature, not per property.** `ExpressionScope` holds
   one feature, its geometry type and the viewport zoom — the three things an
   expression can read — and memoises each pure node it evaluates. The scene
   builder keeps one scope per feature for the whole style, so an expression
   read by five properties across four layers costs one evaluation per
   feature. The counters are measured (`MapRenderer.LastStatistics`, the
   `Render_ExpressionPointsTile` benchmark), not asserted in a comment.

5. **Filters take both dialects.** A filter whose whole subtree is literals
   keeps the legacy operator grammar and its compiled filter model; a filter
   that nests an expression anywhere is an expression filter, which must yield
   a boolean. The two never mix halfway, so the legacy plan stays the legacy
   plan.

## Consequences

- A real client style compiles. What it does not compile is named, and says
  which operator and which path.
- `Spatial.Contracts` is untouched: the dialect is style-model and
  scene-building inside `Spatial.Rendering.Skia`, and no Skia type crosses
  into the pipeline or the contract.
- Symbol **layout** (`text-size`, `text-offset`, `text-field`) stays constant
  for now. Paint is data-driven; layout expressions need the font metrics and
  the placement pass, which is its own bead.
- `to-color`, `at-interpolate`, `cubic-bezier` interpolation, `palette`,
  `format`, `image` and `heatmap`-density are not served. Each is an
  `invalid.arguments` naming the operator.
