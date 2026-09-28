---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
amends: ADR-0070
---

# ADR-0101: A tile with no extent on an axis is rejected, not narrowed

## Context

ADR-0070 put the MVT encoder in `Spatial.Tiling.Mvt`, writing the
protobuf itself so no third-party encoding type crosses the contracts. The
writer places a coordinate in the tile's integer frame by dividing by the
tile's extents:

```csharp
var x = (coordinate.X - bounds.MinX) * extent / bounds.Width;
var y = (bounds.MaxY - coordinate.Y) * extent / bounds.Height;
```

The request was validated only for `Bounds.IsEmpty`, and a zero-extent
envelope is not empty: `Envelope(5, -9, 5, 9)` is a well-formed envelope
whose `Width` is `0`. Dividing by it puts the vertex at `±Infinity`, and
the `(long)` cast of `Math.Round` saturates to `long.MaxValue`, so the
wire delta is `0x7FFFFFFF` — a saturated infinity dressed as a tile
coordinate. A vertical segment over zero-width bounds encodes as
`09 0080400A00FF3F`.

Nothing downstream can notice. The bytes are well-formed protobuf, the
zeros and deltas parse, the tile is served with a `200` and the correct
media type, and the client draws a line of astronomical length off the
tile. That is the worst failure mode in this pipeline: not an error, but
plausible-looking wrong geometry.

Two facts make the tile unrepresentable rather than merely unusual. The
MVT coordinate space is a finite integer lattice of `extent` units per
axis, so a frame with no width has no way to place a vertex in it; and
the schema that made this reachable is a *caller's* choice, not the
scheme's — `scheme.Bounds(tile)` for every scheme in tree is a positive
rectangle, so a degenerate frame means the request itself is not a tile.

## Decision

1. **A tile is renderable only when its bounds have extent on both axes.**
   `MvtTileProjection.HasExtent` is the one test — `Width > 0 &&
   Height > 0` — and `MvtTileService` applies it before a layer is read,
   so a degenerate tile costs no store round-trip. Empty, inverted,
   collapsed and non-finite bounds all fail it, because a non-finite
   bound yields a non-positive or `NaN` extent; the check therefore
   replaces the old `IsEmpty` test rather than adding to it.
2. **A tile that fails it is rejected as `invalid.arguments`**, with the
   same message as every other unrenderable tile ("needs bounds with
   extent on both axes, a CRS and an extent between 1 and 65536"). The
   contract states the rejection so a caller sees a typed failure it can
   act on, not bytes.
3. **The writer does not clamp, and does not guard per vertex.** A
   degenerate-safe frame is possible — pin the collapsed axis to the
   centre line — and it is wrong: it encodes geometry at a position the
   caller never asked for, which is the same class of defect as the
   saturated infinity, one layer of indirection away. The precondition
   is documented on `Project` and enforced once, at the boundary, so the
   per-vertex hot path keeps no branch that only an invariant violation
   can reach.

## Consequences

- The vertical-line-over-a-degenerate-frame case is a `400`-shaped
  failure on the neutral, Esri and OGC vector-tile routes (they all
  funnel into `IVectorTileService.RenderAsync`) instead of a corrupt
  `200`. No scheme in tree can trigger it, so nothing that worked stops
  working.
- The bytes for ordinary tiles are unchanged, and that is pinned: a
  vertical line over a `Envelope(0, -10, 10, 10)` tile still encodes as
  `098020E63C0A00CB39`, so the guard cannot quietly move vertices.
- A future `ITileScheme` that can emit a zero-extent rectangle at some
  zoom now fails loudly at the encoder rather than quietly at the
  client.

## Alternatives

- **Project onto a degenerate-safe frame** (pin the collapsed axis to
  `extent / 2`): rejected — it manufactures a plausible position for
  geometry the request could not place, and the corruption survives.
- **Substitute a tiny epsilon for the extent**: rejected for the same
  reason, and it also makes the frame depend on an arbitrary constant
  rather than on the tile.
- **Clamp the projected coordinate into `[0, extent]`**: rejected — it
  silently distorts every vertex of a legitimate tile whose data sits
  outside the queried rectangle, trading a loud failure for a quiet lie.
- **Leave it to the scheme**: rejected — the scheme owns addressing, and
  a request built by hand bypasses it. The contract is where a
  non-renderable request becomes a typed error.
