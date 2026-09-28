# SpatialEngine-u2x.1 measurement spike — feature query baseline

**The numbers this spike produced are in [`RESULTS.md`](RESULTS.md)**, with the
raw harness output under [`results/`](results).

What this is: a **measurement** harness for the numbers the `SpatialEngine-u2x`
Tier 1 branch is justified by. It times the Feature Service query paths over
the committed world-cities snapshot (34,135 rows — the same rows the demo
store serves as `demo.world_cities`, and the layer the T-015 paging replay
uses) and reports, per path:

- wall time (cold/first call, then p50 and p95 over N iterations),
- **rows materialised** into `FeatureBatch` pages,
- managed bytes allocated per call, and the peak managed heap observed right
  after the call.

## The paths

| # | Path | What it does |
|---|------|--------------|
| A | `ScanAsync` + in-adapter filter | the production path today: `FeatureSpatialMatcher.MatchAsync` materialises every row and filters in the adapter, then `FeatureOrdering` / `FeaturePaging` / `FeatureStatisticsEngine` run over the matched set |
| B | `IFeatureStore.QueryAsync` (bbox + filter pushdown) | the same request with the predicate pushed into the store. **This is what the store can do today**: it filters, but `PostgisFeatures.ReadBatchesAsync` has no row cap or cursor, so every *matching* row is still materialised |
| C | `IFeatureLookup.GetAsync` (by identity) | the per-feature read path: resolve a page of 25 by id instead of scanning |
| D | emulated full pushdown | the **ceiling**, not reachable today: predicate, ordering and `offset`/`limit` all applied in the store, so only the page (or the group rows, or the count) is materialised. `IFeatureStore` has no ordering/limit/projection/aggregation face, which is what `SpatialEngine-u2x.9` proposes |

Path D is an emulation over the same rows the store holds, not a store call —
it is labelled as such in the output so nobody mistakes it for a shipped path.

## Request variants

Each path is measured for the three result shapes the bead names:

- `page25` — bbox + `where` + `orderByFields=population DESC` + `resultRecordCount=25`
  (the ordering and the page both run in the adapter today),
- `countOnly` — `returnCountOnly=true` (ordering still runs first, in the adapter),
- `statistics` — `outStatistics` grouped count + average population by country.

and for two bboxes: `europe` (a selective viewport) and `global` (the whole
world, so the scan cost is identical and only the match count changes).

## Fidelity note

`A` mirrors `FeatureSpatialMatcher.MatchAsync` + `FeatureOrdering.Apply` +
`FeaturePaging.Page` + `FeatureStatisticsEngine.Statistics` in
`AdapterMirror.cs` — same ordinal object-id scheme, the real
`EsriFilterClause` evaluator, the real envelope test, the real page cap
(`min(resultRecordCount, 1000)`). The adapter types themselves are
`internal`, so they cannot be called from here; the mirror is checked against
the served response by `--host=<url>` (the harness asserts the mirrored match
count equals the feature count the real host returns for the same request).

## Running it

```bash
eng/spike-u2x-query-baseline.sh                    # memory store, no Docker
eng/spike-u2x-query-baseline.sh --store=postgis    # real PostGIS in a container
eng/spike-u2x-query-baseline.sh --store=memory --host=http://127.0.0.1:5201
```

Options (all optional): `--store=memory|postgis`, `--connection=…`,
`--iterations=N`, `--warmup=N`, `--reuse` (keep a loaded table instead of
reloading it), `--host=URL` (adds the end-to-end HTTP numbers and the mirror
check), `--label=…`, `--json=<file>`.

The layer under test is `public.spike_world_cities`. The PostGIS run measures
the same table twice: as `PostgisStore.CreateAsync` leaves it, and after the
GiST/btree indexes and `ANALYZE` a deployment would have.
