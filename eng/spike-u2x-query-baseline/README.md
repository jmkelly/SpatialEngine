# SpatialEngine-u2x.1 measurement spike — feature query baseline

> **A baseline, not a description of today.** The store faces this harness
> measured as absent have since landed — the query plan (ADR-0074), pushdown of
> identity and literals (ADR-0097), the store's own aggregates (ADR-0115), paged
> store reads (ADR-0116) and the reduction's group and having (ADR-0128). The
> numbers in [`RESULTS.md`](RESULTS.md) were re-measured on 2026-10-01/02
> against that contract, so the current tables are the ones to quote; the
> original 2026-09-28 run is kept there as an appendix.

**The numbers this spike produced are in [`RESULTS.md`](RESULTS.md)**, with the
raw harness output under [`results/`](results) — the original run in
`results/*.txt` and the 2026-10-01/02 idle re-measurement in
`results/idle/` (with its load averages in
[`results/idle/LOAD.md`](results/idle/LOAD.md)).

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
| A | `ScanAsync` + in-adapter filter | the production path as measured on 2026-09-28: `ScanAsync` materialises every row and the mirror filters, orders, pages and groups in the adapter |
| B | `IFeatureStore.QueryAsync` (bbox + predicate, no cap) | the same request with the plan's restriction pushed into the store. It materialises every **matching** row, exactly as it did on 2026-09-28, so the `rows` column is comparable across the two runs |
| Bp | `IFeatureStore.QueryAsync` with the ordering and the row cap | the plan a served request issues today (ADR-0074 §5, ADR-0116): the page is capped in the store, so only the rows the response needs are built. This is the old `D` ceiling as a shipped path |
| C | `IFeatureLookup.GetAsync` (by identity) | the per-feature read path: resolve a page of 25 by id instead of scanning. **Unavailable on the world-cities layer**, which declares no identity column: the face refuses it with `invalid.arguments` by name (ADR-0140), so the harness reports the refusal instead of a number |
| D | emulated full pushdown | the ceiling for the two shapes no store plan reaches — a count and a grouped aggregate. An emulation over the same rows the store holds, not a store call, labelled as such in the output |

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
`AdapterMirror.cs` — same ordinal object-id scheme, the real predicate
evaluator (`Spatial.Querying.ReferencePredicate`, the reference semantics every
store's pushdown has to agree with, ADR-0074 §4), the real envelope test, the
real page cap (`min(resultRecordCount, 1000)`). The adapter types themselves
are `internal`, so they cannot be called from here; the mirror is checked
against the served response by `--host=<url>` (the harness asserts the mirrored
match count equals the feature count the real host returns for the same
request). A `where` naming the synthetic `OBJECTID` is not answerable by the
mirror — the facade's field overlay is internal — and the spike's own clauses
name an attribute.

## Running it

```bash
eng/spike-u2x-query-baseline.sh                    # memory store, no Docker
eng/spike-u2x-query-baseline.sh --store=postgis    # real PostGIS in a container
eng/spike-u2x-query-baseline.sh --store=memory --host=http://127.0.0.1:5201
eng/spike-u2x-postgis-e2e.sh                       # the end-to-end half: a host serving PostGIS
```

Options (all optional): `--store=memory|postgis`, `--connection=…`,
`--iterations=N`, `--warmup=N`, `--reuse` (keep a loaded table instead of
reloading it), `--host=URL` (adds the end-to-end HTTP numbers and the mirror
check), `--host-service=NAME` and `--host-layer=NAME` (which FeatureServer and
layer `--host` reads; both default to the `demo` service and its `world_cities`
layer), `--label=…`, `--json=<file>`.

`eng/spike-u2x-postgis-e2e.sh` is the end-to-end run on the store a deployment
uses: it starts a throwaway PostGIS, loads the snapshot, adds the deployment
indexes, starts a host whose `postgis` store is that database, publishes the
dataset through the admin publish flow (`PUT /api/maps/spike`) as a
FeatureServer layer, and runs the same requests against it. `--host-service` and
`--host-layer` are what let the harness read a service other than `demo`.

The layer under test is `public.spike_world_cities`. The PostGIS run measures
the same table twice: as `PostgisStore.CreateAsync` leaves it, and after the
GiST/btree indexes and `ANALYZE` a deployment would have.
