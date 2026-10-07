# SpatialEngine-u2x.1 measurement spike — feature query baseline

> **A baseline, not a description of today.** The store faces this harness
> measured as absent have since landed — the query plan (ADR-0074), pushdown of
> identity and literals (ADR-0097), the store's own aggregates (ADR-0115), paged
> store reads (ADR-0116) and the reduction's group and having (ADR-0128). The
> numbers in [`RESULTS.md`](RESULTS.md) were re-measured on 2026-10-01/02
> against that contract and again on 2026-10-07 for the reduction cells
> (SpatialEngine-8dm), so the current tables are the ones to quote; the
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
| B | the store's pushdown for the request | the plan's restriction pushed into the store. For a **page** it is `IFeatureStore.QueryAsync` (bbox + predicate, no cap): on a layer that can carry the plan it materialises every *matching* row, which is what keeps the `rows` column comparable across runs, and on one that cannot it is the whole read plus the reference executor (ADR-0097 §1, ADR-0116 §1). For a **reduction** it is the store's own reduction face (`IFeatureAggregateStore`, probed exactly as the served surface probes it): a count is a `SELECT COUNT(*)` and a grouped reduction a `GROUP BY`, so nothing is materialised at all — even on a keyless layer, because a reduction returns values and no feature (ADR-0184 §1) |
| Bp | B with the plan's ordering and row cap | the page a served request issues today (ADR-0074 §5, ADR-0116): the page is capped in the store, so only the rows the response needs are built. A reduction has no page, so B and Bp are the same call on the `countOnly` and `statistics` variants |
| C | `IFeatureLookup.GetAsync` (by identity) | the per-feature read path: resolve a page of 25 by id instead of scanning. **Unavailable on the world-cities layer**, which declares no identity column: the face refuses it with `invalid.arguments` by name (ADR-0140), so the harness reports the refusal instead of a number |
| D | emulated full pushdown | the reference ceiling for the two reduction shapes — a count and a grouped aggregate — emulated over the same rows the store holds, not a store call, labelled as such in the output. The store's own reduction face (path B) reaches those shapes now, so D is the in-memory cost beside it |

Path D is an emulation over the same rows the store holds, not a store call —
it is labelled as such in the output so nobody mistakes it for a shipped path.

**B and Bp measure a pushdown only where the answer can carry one, and the
line falls differently for a read and a reduction.** A SQL store may only push
a plan where the push is identity-preserving, and a dataset that declares no
identity column names its features by the ordinal of the *read* — so a `WHERE`
in SQL would renumber them (ADR-0097 §1) and an `ORDER BY` with no identity
tie-break is an order an `OFFSET` cannot name (ADR-0116 §1). On such a layer the
store answers a **page** by reading the whole table and finishing it with the
reference executor: the answer is right, and the read is the layer. The
world-cities snapshot is such a layer (no integer identity field, so a table
`CreateAsync` builds from it has no primary key), which is why the PostGIS
`rows` column counts rows *returned* rather than rows read, and why the harness
prints a note saying so on every run where it applies.

The **reductions** are not in that state (ADR-0184 §1): a count and a grouped
reduction return values and no feature, so there is no ordinal for a restriction
to renumber, and their restriction is pushed on the same keyless layer — a
`returnCountOnly` is a `SELECT COUNT(*)` and an `outStatistics` a `GROUP BY`, and
neither reads a row into a feature. Path B is that face, so on the `countOnly`
and `statistics` variants `rows` is **0** on every store, keyless included. An
in-process store has no such limit anywhere: it evaluates the plan over the rows
it already holds, named by the ordinal of that same set. See
[RESULTS.md](RESULTS.md#the-postgis-rows-column-is-not-what-the-database-read).

## Request variants

Each path is measured for the three result shapes the bead names:

- `page25` — bbox + `where` + `orderByFields=population DESC` + `resultRecordCount=25`
  (the ordering and the page both run in the adapter today),
- `countOnly` — `returnCountOnly=true`; path B/Bp is the store's reduction face
  (`SELECT COUNT(*)`), and path A still orders and counts in the adapter,
- `statistics` — `outStatistics` grouped count + average population by country;
  path B/Bp is the store's grouped reduction (`GROUP BY`), pushed because the
  statistics plan states the group key as its order (a `GROUP BY` returns rows
  in no defined order on its own). A served `outStatistics` request states that
  order by naming the group field in `orderByFields`; without it the surface
  keeps the match path, which is SpatialEngine-d0q.

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
