# SpatialEngine-u2x.21.2 — is per-layer tile composition worth building?

**Decision: yes — cache per-layer tiles and composite them at serve time,
compositing from per-layer PNGs. Recorded in ADR-0116.**

The measurement bead behind ADR-0083's deferred decision. Run it with:

```
eng/spike-u2x-tile-cache.sh --repeats=3
```

Raw output lands in `artifacts/spike-u2x.21.2/`. This file is the reading of it.

## The map

A five-layer city basemap (landuse, buildings, roads, waterways, places) over a
0.35° × 0.20° metro extent, published through the in-memory store's own
create/write faces, 25 tiles at z12 — the working set one client pulls for a
view. Arm A is the shipped design (ADR-0046 + ADR-0083) and is measured through
the real renderer, tiling scheme, store and version fold. Arms B and C are
labelled emulations of designs that are not shipped; no product code changes.

## The number

**A single-layer data edit throws out every warm tile of the map — 25 of 25 —
while exactly one tile's pixels actually changed. A single-layer style save
invalidates 25 of 25, and all 25 genuinely changed, so style saves are not
over-invalidated at all.**

| | value |
| --- | --- |
| over-invalidation, single-layer edit | 25.0× (every layer, worst and sparsest alike) |
| over-invalidation, single-layer style save | 1.0× |
| whole-map cold re-render, per tile | 77.5 ms CPU, 7.74 MB |
| whole-map warm hit, per tile | 0.00 ms CPU |
| serve-time composite, per tile | 38.6 ms CPU from per-layer PNG, 64.1 ms from encoded layers |

The fan-out counts warm entries *dropped*, not tiles *re-rendered*: both arms
are demand-filled, so an invalidated tile costs something only when a client
asks for it again. 25× is the ceiling on the waste, not the bill.

## Which timing basis, and why it is CPU

The measurement box is shared with a parallel swarm, and the wall-time
**ordering** of the two arms flipped between runs — the composite was cheaper
than a whole-map cold render in two runs of three and dearer in the third. A
basis that reorders between runs cannot decide anything, so the spike stopped
asserting a trustworthy basis and started measuring one: every sample carries
CPU time, and the contention probe manufactures load with external processes
and re-measures both arms.

| basis | drift under load, two runs |
| --- | --- |
| wall time | 74%, 109% |
| CPU time | 28%, 13% |

The probe's load is applied by *separate* processes deliberately. An in-process
burner would pour straight into this process's `TotalProcessorTime` — the
measurement — and CPU time under load would read ~17× idle: a contaminated
number dressed as evidence about the probe.

Allocation is reported but demoted to a labelled proxy. The composite's
expense is Skia blend plus PNG encode, which is CPU in existing buffers and
allocates almost nothing (0.072 MB against a 7.74 MB cold render), so the
allocation basis flatters the per-layer arm and cannot stand in for cost.

## Does it repay itself

Break-even is **tiles served per edit** — lower is better, ∞ never repays.
Per-layer composition re-renders the edited layer once and then pays a
composite tax on every serve; whole-map entries pay a cold render on every
serve after an edit.

| edited layer | re-render (CPU) | break-even, per-layer PNG | break-even, per-layer raw |
| --- | --- | --- | --- |
| basemap.landuse | 15.9 ms | 1.19 | 0.41 |
| basemap.buildings | 32.6 ms | 2.44 | 0.84 |
| basemap.roads | 36.3 ms | 2.71 | 0.93 |
| basemap.waterways | 13.1 ms | 0.98 | 0.34 |
| basemap.places | 12.0 ms | 0.90 | 0.31 |

Amortised totals, worst-case layer, for a deployment serving R tiles per edit:

| tiles served per edit | A | B (per-layer PNG) | B (per-layer raw) |
| --- | --- | --- | --- |
| 1 | 136.8 ms | 183.8 ms | 145.4 ms |
| 5 | 683.9 ms | 668.6 ms | 476.4 ms |
| **25 (one viewport)** | **3419.5 ms** | **3092.3 ms** | **2131.5 ms** |
| 100 | 13677.8 ms | 12181.1 ms | 8338.2 ms |

Per-layer composition repays itself once more than about one to three tiles
are served between edits, and at the realistic profile of a client pulling one
viewport between edits it saves **10% of tile CPU compositing from PNGs and 38%
compositing from raw buffers**.

## Why per-layer PNG and not raw

The cheap blend arm is cheap only because it blends from already-decoded RGBA,
which a cache holding raw buffers must store. That is a different amount of
memory:

| | whole-map | per-layer PNG | per-layer raw |
| --- | --- | --- | --- |
| entries for the working set | 25 | 125 (5×) | 125 (5×) |
| bytes | 1.32 MB | 1.43 MB (1.1×) | 31.25 MB (23.7×) |
| bytes at the 4096-entry bound | — | — | 1024 MB against a 64 MB bound |

Raw per-layer storage would overrun the byte bound by 16× and cut effective
capacity 5×, to buy a 28-point improvement in a saving that is already positive
without it. So the decision is per-layer PNGs, taking the weaker but real win.

## What the decision is not

- Not a licence to widen invalidation. A style save already moves the key
  correctly and re-renders only what it must (1.0×); nothing about
  composition changes that.
- Not driven by the 25× alone. That number is a ceiling on waste that only
  materialises for tiles a client actually re-requests, and the case rests on
  the amortised totals.
- Arm C (a per-layer *style* sub-key) refactors the key without changing the
  entry, so it cannot narrow the fan-out at all — measured, not assumed: it
  reports arm A's numbers. There is no third design here.
