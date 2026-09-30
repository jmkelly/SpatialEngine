---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
summary: `to-color`, `at-interpolate` and `cubic-bezier` are style-dialect nodes; `to-color` is the one coercion and is asked for by name.
---

# ADR-0088: `to-color`, `at-interpolate` and `cubic-bezier` in the style dialect

## Context

ADR-0076 made the MapLibre style dialect a compiled, typed expression tree
rather than a constant-plus-nine-filters grammar, and listed the constructs it
deliberately did not serve. Three of them are interpolation-shaped rather than
new machinery, and by the time this decision was taken the machinery was
already there: the evaluator had `interpolate` over numbers and colours, a
typed stop list, a compile-time type unification over the stop outputs and a
rejection discipline that names the offending operator and path. The three
constructs wanted *depth*, not surface.

- **`to-color`** — the idiom "ramp a number into a colour". ADR-0076 refused
  it on purpose: a bare number is not a colour and the compiler will not
  coerce silently, so `["interpolate", ["linear"], ["zoom"], 5, 0, 10, 1]` on
  `fill-color` is rejected. That rejection is right and stays. What was missing
  is the *opt-in* form: MapLibre writes the same ramp as
  `… 5, ["to-color", 0], 10, ["to-color", 1]`, and that is a request to coerce,
  not an accident of one.
- **`at-interpolate`** — the value of an interpolation at a named stop rather
  than at the input. It is what makes a ramp reusable: bind it once with
  `let`, then read the same ramp at 5, 10 and 15. Today the stop list can only
  be resampled by rewriting it, and there is no node that can do that.
- **`cubic-bezier` interpolation** — the CSS easing curve
  `["cubic-bezier", x1, y1, x2, y2]`. The evaluator has a bracket, a progress
  and a mix; it has no way to shape the progress.

## Decision

1. **`to-color` is the one coercion, and it is asked for by name.** A number
   is clamped to [0, 1] and written to all three channels; a colour name
   (`"red"`, `"#ff0000"`, `"rgb(…)"`) parses to itself. The node is typed
   `Color`, which is what makes the MapLibre idiom work: because the stop
   outputs unify to `Color`, an `interpolate` whose stops are all `to-color`
   mixes per channel like any other colour ramp. A number on a colour property
   is *still* rejected — `to-color` changes where the conversion is written
   down, not whether it happens. A string that is neither a number nor a
   colour, and a `to-color` of something already a colour, are typed
   `invalid.arguments` naming the operator.

2. **`at-interpolate` samples the same ramp at a literal.** It is
   `["at-interpolate", kind, input, at, stop, output, …]`: the same easing and
   the same stop list as `interpolate`, with the sampled point read from a
   number operand rather than from the input. Both share one sampler, so the
   clamping outside the stop range, the colour and number semantics and the
   type unification are literally the same code. The input is still evaluated
   — it is the domain the stops are written in, and a feature it cannot supply
   for has no answer here either — but it is not the sample point.

3. **`cubic-bezier` is the CSS definition, solved not tabulated.** The eased
   progress is the *ordinate* of the cubic Bezier (0,0) → (x1,y1) → (x2,y2) →
   (1,1) at the parameter where the *abscissa* is the linear progress. The
   abscissa is solved by Newton with a bisection guard (it is monotonic
   because x1 and x2 are required to lie in [0, 1]), so the ordinates stay
   unconstrained exactly as CSS allows them to be, and no sample table is
   carried per feature. A curve whose two control points agree is the identity
   and is not solved at all, which is the `["cubic-bezier"]` default. Trailing
   control values may be omitted and default to zero, as in the spec.

4. **The typed rejection discipline is unchanged.** A malformed easing — a
   non-numeric control point, five of them, an abscissa outside [0, 1] — is a
   compile-time `invalid.arguments` naming `cubic-bezier` and the path; a
   non-numeric `at` is one naming `at-interpolate`; `to-color` of an
   unreadable operand is the same at render time, as a value only knowable per
   feature always was. Nothing is silently ignored, and a bare
   `["cubic-bezier", …]` in operator position is still an unknown operator.

5. **Conformance is hand-computed, not golden.** The cubic-bezier rows are
   pinned on the closed form: when x1 + x2 = 1 the curve passes X(0.5) = 0.5,
   and Y(0.5) = 0.375·(y1 + y2) + 0.125, so `(0,1,1,1)` yields 0.875 and
   `(0,0,1,0)` yields 0.125 where linear yields 0.5. Nothing in the contract
   depends on a recorded image.

## Consequences

- A client style that ramps an attribute into a colour, reuses one ramp as a
  scale, or eases its zoom ramp compiles and evaluates. What still does not
  compile is named, and says which operator and which path.
- `palette`, `format`, `image`, `downsample` and `heatmap`-density remain
  `invalid.arguments` naming the operator. `linear` and `exponential` behave
  exactly as before, so no style that compiled before renders differently.
- Symbol **layout** expressions (`text-size`, `text-offset`, `text-field`,
  `text-anchor`) stay constant; that is its own bead, and the three operators
  here are available to it when it lands.
- `Spatial.Contracts` is untouched: `CubicBezier`, `Easing` and the sampler
  are style-model types inside `Spatial.Rendering.Skia`.
- The Bezier solve is bounded at 24 iterations and is only reached by a style
  that asked for an eased ramp, so the constant-style hot path is unchanged.
