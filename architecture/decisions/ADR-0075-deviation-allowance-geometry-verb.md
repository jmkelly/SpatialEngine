---
status: proposed
date: 2026-09-27
deciders: maintainer + agent
---

# ADR-0075: A deviation allowance is its own geometry verb, not a simplify tolerance

## Context

The Feature Service `query` (spec §9.1.4) carries two parameters that change
the response geometry and one algorithm parameter, and the adapter treated all
three the same way:

- `geometryPrecision` rounds every ordinate to a number of decimal places —
  served, by `GeometryRounding`.
- `maxAllowableOffset` was parsed, range-checked, carried on the query record
  and read by nothing. A client that set it got full-precision geometry and no
  error.
- `quantizationParameters` was rejected by name with a typed
  `invalid.arguments`.

The underlying verb existed: `IGeometryOperations.Simplify` is Douglas-Peucker
generalization, and ADR-0036 already routed the Geometry Service's
`generalize` and `simplify` operations to it. What was missing was a
*distinct* verb for the other meaning.

The trap is that the two parameters are not the same number. `simplify`'s
`deviation` is an **algorithm parameter**: Douglas-Peucker drops a vertex when
it lies within that many units of the chord replacing it, and the caller is
asking the algorithm for a particular coarseness. `maxAllowableOffset` is a
**budget**: the caller is saying "you may deviate by at most this much, and
you may deviate by less". Reusing the one method for both makes the two
meanings swappable at a call site, and a caller who then asks for a *smaller*
simplification than the offset is over-simplifying; a caller who asks for a
larger one breaks the guarantee the parameter names.

The bead named the second half of the trap: Douglas-Peucker's tolerance does
not directly bound vertex displacement, so a *vertex-displacement-constrained*
generalization is the honest engine verb. That is the stronger statement than
the tolerance, and it is what the parameter promises.

## Decision

**`IGeometryOperations.Generalize(geometry, maxDisplacement, cancellationToken)`**
(new method on the existing face; no new interface, no new package, and
`Spatial.Contracts` still references only `Spatial.Core`).

The contract it states:

- Every returned vertex is a vertex of the input. The verb never invents a
  position, so a feature's stored coordinates are never moved.
- Every input vertex lies within `maxDisplacement` of the returned geometry.
- The allowance is a **ceiling, not a licence**. An allowance that cannot be
  spent without changing the geometry's kind — Douglas-Peucker empties a small
  ring at a large allowance, and rewrites a ring's closure at any allowance,
  re-appending the first coordinate — returns the input unchanged. A zero
  allowance therefore returns the input, which is the only answer compliant
  with "deviate by at most zero".
- A non-finite or negative allowance is `invalid.arguments`; cancellation is
  honoured before the algorithm runs.

**Implementation** (`NtsGeometryOperations`): Douglas-Peucker at
`maxDisplacement`, with the ceiling guard. It is deliberately not a new
algorithm — the existing DP test already guarantees the per-vertex bound — but
a new *meaning* with its own name, so the two parameter meanings cannot be
swapped at a call site. `Simplify` keeps its unguarded behaviour for the
Geometry Service, which has no reason to protect a client's feature shape.

**The adapter** now spends both parameters through this one verb
(`GeometryGeneralization`), on the response geometry after `outSR` reprojection
and `geometryPrecision` rounding, in the wire order:

| Request | Response |
| --- | --- |
| `maxAllowableOffset=n` | `Generalize(geometry, n)` |
| `quantizationParameters={tolerance, extent, originPosition, mode}` | snap every ordinate (x, y, z and m) to the view grid, then `Generalize(…, tolerance)` |
| both | the allowance first — it is stated in the geometry's own units — then the grid |
| neither | the stored geometry, untouched (same instance) |

**The quantization grid** (`EsriQuantization`) is anchored on the view extent:
`xmin` for x, and `ymax` under `upperLeft` (the spec default) or `ymin` under
`lowerLeft` for y. z and m quantize on the same value quantum, which is what
makes it a distance grid rather than a per-axis decimal rounding. The
follow-up generalization is what removes the staircase the grid introduces;
the composed deviation is at most the offset plus half the quantum.

**Refusals, each naming its reason** (`invalid.arguments`): unparsable JSON, a
non-object, a missing or non-positive `tolerance`, a missing or degenerate
`extent`, and a `mode` or `originPosition` outside the served values. The
repo's silent-ignore policy (T9) is why the last two are named rather than
ignored: a `mode` the engine does not implement must not look like one it did.

## Consequences

- One new method on an existing contract face and one new implementation
  method. No new interface, no new package, no new DI registration, and the
  host stays JIT-compiled.
- `maxAllowableOffset` is honoured: a client setting it gets geometry within
  the offset, and a client that does not is unaffected. This changes response
  bytes for requests that set the parameter, which is the point.
- `quantizationParameters` is served instead of rejected. A client that
  previously received a typed 400 now receives a quantized geometry; one that
  previously received full precision *despite asking for quantization* now
  receives what it asked for.
- The deviation bound is a statement the tests measure, not a comment: the
  adapter test measures every input vertex of a 1 km ring against the
  response, and the verb test does the same against the implementation.
- The verb is strictly weaker than a caller might hope for in one respect, and
  it is deliberate: the guard means a huge allowance returns the *input*, not
  a maximally thinned shape. That is the correct reading of "at most", and it
  is what keeps a feature's geometry kind stable across clients.
- `FeatureProjection.TransformFeature` gained an `IGeometryOperations`
  argument, threaded from the four call sites that shape a response. Every one
  of them already had the operations in hand.
- `supportsQuantization` is still not advertised on the layer metadata; the
  layer response advertises no such capability flags at all today, so the
  ArcGIS REST JS `supportsQuantization` gate stays false. That is a metadata
  gap, tracked separately, not part of this decision.

## References

- ADR-0005 (implementation types stay in their implementation)
- ADR-0033 (in-process service interfaces)
- ADR-0035 (GeoServices boundary adapter)
- ADR-0036 (geometry measure/processing/relation verb faces)
- `architecture/references/geoservices-compatibility.md` §2, §7.1 (T8)
- `research/arcgis/conformance-sources.md` T8
