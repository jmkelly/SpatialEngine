---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
summary: The tile cache holds one entry per (tile, layer), keyed by a per-layer content version, and composites the layers at serve time.
---

# ADR-0117: The tile cache holds per-layer tiles, composited at serve time

## Context

ADR-0083 fixed the correctness half of cache invalidation by folding a
per-dataset data version into the tile cache key (ADR-0046). It deliberately
left the composition half open, and its consequences section says so: "A tile
renders the whole map, so a write to one layer still invalidates that map's
tiles, not only the affected layer's. That is the pre-existing composition
granularity, unchanged by this ADR; per-layer composition is a separate,
measured decision."

That decision is this ADR. The measurement is
`eng/spike-u2x-tile-cache`, and its reading is in
`eng/spike-u2x-tile-cache/RESULTS.md`. Nothing here is inferred: the numbers
below come from the spike's own output.

The shape of the problem is that a rendered tile is a composition of its
layers, but the cache stores the composition. So one layer's data edit
invalidates every tile of the map that contains it.

## Measurement

A five-layer city basemap (landuse, buildings, roads, waterways, places) over
a 0.35° × 0.20° metro extent, published through the store's own write faces,
with a 25-tile z12 working set — what one client pulls for a view. Arm A is
the shipped design and is measured through the real renderer, tiling scheme,
store and version fold; arm B is an emulation of the design this ADR adopts.

- A single-layer **data edit** invalidates **25 of 25** warm tiles while
  **one** tile's pixels actually changed: 25× over-invalidation, for every
  layer, sparsest included.
- A single-layer **style save** invalidates 25 of 25 and all 25 genuinely
  change: **1.0×**. Style saves are not over-invalidated at all, which is why
  this is a decision about data edits only.

Per-tile CPU cost, and the amortised total for R tiles served per edit:

| | per tile, CPU | R=1 | R=5 | R=25 (one viewport) |
| --- | --- | --- | --- | --- |
| A whole-map cold render | 77.5 ms | 136.8 ms | 683.9 ms | 3419.5 ms |
| B composite from per-layer PNG | 64.1 ms | 183.8 ms | 668.6 ms | 3092.3 ms |
| B composite from per-layer raw | 38.6 ms | 145.4 ms | 476.4 ms | 2131.5 ms |

Break-even is **tiles served per edit**, and it is 0.9–2.7 for per-layer PNGs.
Per-layer composition repays itself once more than roughly one to three tiles
are served between edits, and at a realistic profile — a client pulling one
viewport between edits — it saves 10% of tile CPU compositing from PNGs.

Two measurement notes, because the bead's number had to survive a machine
shared with a parallel swarm. First, the wall-time **ordering** of the arms
flipped between runs of the spike; the harness therefore carries CPU time and
runs a contention probe that manufactures load with external processes, and CPU
drifts 13–28% under load where wall drifts 74–109%. Second, allocation is
reported but is not the basis: the composite's expense is Skia blend plus PNG
encode, which is CPU in existing buffers and allocates almost nothing, so
allocation flatters arm B and cannot stand in for cost.

## Decision

**The tile cache holds one entry per (tile, layer) and composites the layers
into the served tile at request time.**

- The key carries a *per-layer* content version rather than one folded version
  for the whole map, so a write to one layer misses that layer's entries and
  nothing else. ADR-0083's `ContentVersions.FoldAsync` folds the whole request
  for one key; here each layer's entry is keyed on its own dataset's version.
- The per-layer entries are **PNG**, the same representation arm A caches, so
  the change to existing entries is where they are written and read rather than
  in their format. Compositing from encoded layers costs 64.1 ms CPU per tile
  against the raw-buffer arm's 38.6 ms, and the raw arm is not available: it
  requires storing a 256×256 RGBA buffer per (tile, layer), which is 23.7× the
  bytes for this working set and would need 1024 MB to fill a 4096-entry cache
  against a 64 MB byte bound. Paying 28 more points of CPU to overrun the byte
  bound by 16× and cut effective capacity 5×, for a saving that is already
  positive without it, is the wrong trade.
- The composite is a serve-time cost on **every** tile response, including
  warm hits, which arm A answers from the cache. That standing tax is the
  price of the design and is the reason the break-even is a ratio and not a
  certainty: below about one to three tiles served per edit, whole-map entries
  are cheaper, and that regime is unchanged and still correct.

**Arm C is rejected by measurement, not by argument.** A per-layer *style*
sub-key refactors how the version is folded without changing the entry, so it
cannot narrow the fan-out; the spike measures it and reports arm A's numbers.
There is no middle design that gets the invalidation win for free.

## Consequences

- A write to one layer now invalidates that layer's tiles for that map, not
  every tile. This is the whole point, and it is the first change to the tile
  cache's entry shape since ADR-0046.
- **Every tile response pays a composite** even when all layers are warm.
  Whole-map entries answer a warm hit in 0.00 ms CPU. Hosts under a
  tiles-per-edit ratio below roughly 1–3 will be slower than they are today,
  and this is a deliberate, measured trade rather than an oversight.
- A per-layer cache holds 5× the entries for the same tiles, so the entry
  bound buys one fifth as much map coverage per map. The byte bound is
  essentially unchanged, because per-layer PNGs are 1.1× arm A's bytes.
- Style saves are unaffected: they already moved the key per layer
  (ADR-0083), and they were never over-invalidated.
- Clients are unaffected on the wire. The composite happens server-side behind
  the existing tile routes; `X-Tile-Cached` and `X-Tile-Version` keep their
  meaning, because what is cached and what is served are the same pixels.
- The SDK's own tile cache, if a client caches tiles across edits, still sees
  `X-Tile-Version` move on a per-layer edit — now for the layer that changed,
  which is a subset of the tiles it holds rather than all of them.

## Alternatives

- **Keep the whole-map flush (do nothing).** Zero new cost and zero risk, and
  for a cache serving under about one tile per edit it is the faster design.
  Rejected for a map service in general, because the measurement shows the
  composition design ahead from roughly two tiles per edit upward, and an
  editing session sits well above that.
- **Cache per-layer raw RGBA buffers.** The fastest composite, at 38.6 ms
  against 64.1 ms, and 0.31–0.93 tiles per edit instead of 0.9–2.7. Rejected on
  the memory: 23.7× the bytes, 1024 MB to fill a 4096-entry cache against a
  64 MB bound. Revisit if the byte bound or the tile size changes.
- **A per-layer style sub-key, no per-layer entry (arm C).** Measured to be
  arm A's numbers: it cannot narrow fan-out, because one entry per tile still
  holds the whole composition.
- **Let clients cache per-layer tiles and compose in the browser.** Removes the
  server composite tax entirely and is a real alternative to this ADR, but it
  publishes per-layer raster endpoints and moves blend and encode onto every
  client device. Not decided here; worth a bead if the server composite tax
  proves expensive in production.

## See also

ADR-0083 (data version in the key), ADR-0046 (tile cache key and pluggable
cache), ADR-0044 (raster rendering pipeline), ADR-0070 (MVT over the same
cache), `eng/spike-u2x-tile-cache/RESULTS.md`.
