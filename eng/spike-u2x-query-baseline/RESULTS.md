# Results — SpatialEngine-u2x.1

Re-measured on **2026-10-01/02** (the original run was 2026-09-28; its
load-contaminated figures are kept in the [appendix](#appendix-the-2026-09-28-run)
so the two can be compared). The PostGIS and in-memory tables were re-measured
again on **2026-10-07** (SpatialEngine-8dm): their `countOnly` and `statistics`
cells were the pre-ADR-0184 figures, and the harness now routes a reduction
through the store's own reduction face rather than reducing a read in the
adapter.

Layer: the committed GeoNames world-cities snapshot, **34,135 rows**
(`demo.world_cities`, CC-BY 4.0), loaded through the store's own create/write
faces into `public.spike_world_cities`.

Request in every cell: `where=population > 100000`, an
`esriGeometryEnvelope` bbox, `orderByFields=population DESC`,
`resultRecordCount=25` (`page25`), plus `returnCountOnly` (`countOnly`) and a
grouped `outStatistics` (count + avg population, `groupBy=country`). Two bboxes:
`europe` = −10,36,5,55 (**283** matches) and `global` = the world
(**6,253** matches).

## Method, and what is trustworthy

The box is shared with a parallel agent swarm and was never idle: load average
ranged **1.4 to 12.4** on 12 cores across these runs (the 2026-09-28 run sat at
100–120). Every load reading is recorded in
[`results/idle/LOAD.md`](results/idle/LOAD.md). What changed is the estimator:

- **minimum p50 of three independent repeats**, not the p50 of one run. A
  neighbour's load can only ever add time, so the minimum is the estimator that
  survives it, and the spread of the three is reported next to it.
- 30 iterations and 5 warmup for the in-process paths, 10 and 3 for the
  2026-10-07 PostGIS re-run, 20 and 3 over HTTP.
- the `rows`, `result` and `alloc_MB` columns are exact and identical in every
  run of every repeat, as before.

The 2026-09-28 run's own numbers were "the least-contended p50 of four runs"
chosen by hand, with a run-to-run p50 spread of 2–5×. The spread here is
**1.1–1.6× in-process and 1.1–2.2× over HTTP**, so these numbers are
trustworthy to roughly ±20%, not ±250%.

## The harness moved, so the columns moved with it

The committed harness had not compiled since 2026-09-28 (`EsriFilterClause` is
now `EsriWhere`, and `IFeatureStore.QueryAsync` takes a `FeatureQuery` plan and
answers a `FeatureQueryPage`), so it was ported to today's contract before
anything was measured. Two consequences for reading the tables:

- **`B` is no longer the same call it was.** On 2026-09-28 `B` was
  `QueryAsync(dataset, bbox, filterString)` and every *matching* row came back
  (ADR-0074/ADR-0116 did not exist). `B` below is the same intent — the bbox
  and the attribute predicate pushed into the store, no page cap — and the
  **rows** column is the same measurement. Its wall time is not comparable with
  the old `B` cell for cell. It changed once more on 2026-10-07
  (SpatialEngine-8dm): a **reduction** (`countOnly`, `statistics`) now measures
  the store's own reduction face (`IFeatureAggregateStore`, through
  `FeatureReductionFallback`), which is the shipped path for those shapes, so
  their `rows` is 0 and their allocation is the reduction's rather than a whole
  read's. A **page** (`page25`) is still `IFeatureStore.QueryAsync`.
- **`Bp` is new**: the plan a served request actually issues today, with the
  ordering and the row cap in it (ADR-0074 §5, ADR-0116). It is the old `D`
  ceiling as a shipped path, not an emulation, so it is reported separately and
  `D` is kept beside it for continuity. On a PostGIS **feature page** it is
  *not* a ceiling, because this layer cannot carry the plan; a reduction has no
  page, so `B` and `Bp` are the same call on the `countOnly` and `statistics`
  rows. See [below](#the-postgis-rows-column-is-not-what-the-database-read).
- `C` is reported as **unavailable** rather than as a number: a dataset that
  declares no identity column is refused with `invalid.arguments` by name
  (ADR-0140), where on 2026-09-28 it answered empty.

## PostGIS, in-process — the store a real deployment uses

p50 ms and MB/call over **10 iterations** (`--iterations=10 --warmup=3`),
indexed table (GiST geometry + btree on `population` and `country`,
`ANALYZE`), minimum p50 of three repeats in
`results/idle/postgis-0184-r{1,2,3}-indexed.txt`. The table was re-measured on
**2026-10-07** (SpatialEngine-8dm) because its reduction cells were the
pre-ADR-0184 figures; the plan PostgreSQL chose is in
[`results/idle/postgis-0184-r1-indexed-plan.txt`](results/idle/postgis-0184-r1-indexed-plan.txt)
(a `BitmapAnd` of the GiST scan and the `population` btree).

> The `rows` column counts what the call **returned**. On this layer a
> **feature page** (`page25`) is answered by a whole-layer read, because the
> layer declares no identity column (ADR-0097 §1, ADR-0116 §1): its `alloc_MB`
> follows the whole read whatever the page size. A **reduction** (`countOnly`,
> `statistics`) is not a read — a count and a grouped reduction return values
> and no feature, so their restriction is pushed on this same keyless layer and
> their `rows` is **0** (ADR-0184 §1). Read
> [the section on it](#the-postgis-rows-column-is-not-what-the-database-read)
> before quoting these cells as a pushdown.

| scenario / variant | A `ScanAsync`+filter | B store pushdown | Bp `B` + order/cap | D emulated ceiling | rows A / B / Bp |
|---|---|---|---|---|---|
| europe / page25 | 176 ms / 31.11 MB | 156 ms / 31.45 MB | 178 ms / 31.39 MB | 9.1 ms / 0.81 MB | 34135 / 283 / 25 |
| europe / countOnly | 131 ms / 31.10 MB | **2.4 ms / 0.01 MB** | **2.5 ms / 0.01 MB** | 7.1 ms / 0.78 MB | 34135 / 0 / 0 |
| europe / statistics | 136 ms / 31.09 MB | **2.8 ms / 0.02 MB** | **2.8 ms / 0.02 MB** | 6.4 ms / 0.78 MB | 34135 / 0 / 0 |
| global / page25 | 147 ms / 31.91 MB | 136 ms / 32.69 MB | 142 ms / 31.51 MB | 13.2 ms / 1.31 MB | 34135 / 6253 / 25 |
| global / countOnly | 132 ms / 31.91 MB | **5.9 ms / 0.01 MB** | **5.9 ms / 0.01 MB** | 5.2 ms / 0.78 MB | 34135 / 0 / 0 |
| global / statistics | 127 ms / 31.58 MB | **8.8 ms / 0.08 MB** | **8.6 ms / 0.08 MB** | 6.6 ms / 0.79 MB | 34135 / 0 / 0 |

Repeat spread on the p50: 176–403 ms (europe page25 A), 156–368 ms (europe
page25 B), 178–589 ms (europe page25 Bp). The box carried load **4.7–9.4** on
12 cores during the re-run (recorded in
[`results/idle/LOAD.md`](results/idle/LOAD.md)); the minimum of the three is
the estimator that survives it.

**What this says, and what it revises.**

1. **The rows argument stands for the page, and the reductions now walk away
   from it.** The scan builds 34,135 rows and ~31 MB for a 25-row page, every
   request, and again for every page of a walk; the paged plan builds **25
   rows**. That is exact and load-independent, as a count of rows *returned*.
   A reduction is the case where the argument was always going to lose: it
   returns no feature at all, so both the rows and the whole read behind them
   disappear — `countOnly` and `statistics` are **2–9 ms and 0.01–0.08 MB**,
   against a `page25` still in the 136–178 ms / ~31 MB band.
2. **On the feature page the wall time is a wash, and that is unchanged.** A, B
   and Bp all land in the same ~140–180 ms band (the load-contended europe
   cells aside). The database is not the bottleneck for a page: most of every
   cell is row mapping and `FeatureBatch` construction, which the scan also
   pays. The honest summary is still that **materialisation** — not wall-clock —
   is what the page's pushdown work was justified by.
3. **Allocation now follows the work, not the layer.** Before ADR-0185 a 25-row
   page allocated **46–48 MB**, more than the full scan's 31 MB, because the
   keyless read mapped every row into a second feature and sorted the whole
   read to name the page. The `page25` cells are now **31.4–32.7 MB**, within a
   few per cent of `A`, and the reduction cells are two orders of magnitude
   below that. The whole read is still the price of a keyless *page*
   (ADR-0184 §2); it is no longer the price of a count (ADR-0184 §1).

## The PostGIS `rows` column is not what the database read

The world-cities snapshot carries no integer identity field, so a table the
store's own `CreateAsync` builds from it declares no primary key
(`idColumns=[]` in the header of every PostGIS run above). A dataset like that
names its features by **the ordinal of the read**, and that rule reaches the
*read* faces, not the *reduction* faces:

- a **feature read** may only be pushed where the push is identity-preserving,
  so a `WHERE` that reached SQL would renumber every feature after the first
  match (ADR-0097 §1, and its consequence for both SQL stores), and a page is
  addressable only when the plan's order is an `ORDER BY` the table can make
  total, identity tie-break included (ADR-0116 §1). So a page on this layer is
  a **whole-layer read plus an in-memory select, sort and page** — the same
  34,135 rows `A` reads, narrowed after the fact. That is why `page25`'s
  `alloc_MB` is ~31 MB (the whole read) while its `rows` is 25 (rows
  *returned*), and why the two do not move together;
- a **reduction** is not a feature read. A count, a distinct set and a grouped
  reduction return values and no feature, so there is no ordinal for a `WHERE`
  to renumber, and the restriction is pushed on the same keyless layer
  (ADR-0184 §1). `returnCountOnly` is a `SELECT COUNT(*)`, `outStatistics` a
  `GROUP BY`, and nothing is read into a feature. That is why the reduction
  cells above are **0 rows and 0.01–0.08 MB**: the store's own reduction face
  (`IFeatureAggregateStore`, probed through `FeatureReductionFallback` exactly
  as the served surface probes it) answered them, and path `B` is that face.
  One restriction still stays in the caller — an identity restriction a dataset
  cannot state — and the feature read's own decline is untouched, page and all
  (ADR-0184 §2). The grouped reduction is a `GROUP BY` only when the plan's
  order is over the **group key itself** — a `GROUP BY` returns rows in no
  defined order (ADR-0133 §6) — and the harness's statistics plan states it;
  without that order the store still pushes the restriction and groups the
  restricted rows in managed code (0.34 MB europe / 7.22 MB global). A served
  `outStatistics` request states the group order by naming the group field in
  `orderByFields`, and a plain `outStatistics` + `groupByFieldsForStatistics`
  with no `orderByFields` keeps the match path entirely — a routing gap filed
  as SpatialEngine-d0q, not fixed here;
- the in-process `MemoryStore` numbers below *do* follow the rows for a page
  (Bp 0.80 MB for 25 rows): an in-process store evaluates the plan over rows it
  already holds and names them by the ordinal of that same set, so nothing it
  does renumbers anything. Its reduction face is the same reference executor,
  so its reduction cells fall too, but only as far as scanning every row in
  memory allows — `global statistics` still allocates 2.40 MB.

Before ADR-0184 and ADR-0185 the same cells read differently: on 2026-10-05
(SpatialEngine-d20, `--iterations=10 --warmup=3`) **every** `B`/`Bp` cell on
this layer allocated 46.74–48.75 MB, `countOnly` and `statistics` included,
because each was a whole read that mapped every row into a second feature and
then reduced or paged it in managed code. Bisecting one call with
`GC.GetTotalAllocatedBytes` around each stage put the whole figure in the
fallback read: the description is cached (0.00 MB), the predicate compiles in
0.00 MB, and the same statement through raw Npgsql allocates 0.01 MB. That read
is still the documented answer for a keyless **page**, not a leak in the plan;
for a **reduction** it is now removed, which is what the 0.01–0.08 MB cells
measure.

**What would measure a feature-read pushdown:** a PostGIS layer that declares an
identity column. Nothing in the store needs to change for that — the same plan
on such a layer takes the `ORDER BY`/`LIMIT` path (`PostgisPlanPagingTests`
pins which plans those are). The harness now says this in the report itself, so
the number cannot be read the wrong way again. The **reductions** no longer wait
for that layer: they are pushed on the keyless one, and the table above is that
measurement.

## In-memory store (what `eng/seed.sh` and CI use)

Minimum p50 of `results/idle/memory-0184-r{1,2,3}.txt`, 30 iterations,
re-measured 2026-10-07 (SpatialEngine-8dm) because the reduction cells moved
with the store pushdown.

| scenario / variant | A `ScanAsync`+filter | B store pushdown | Bp `B` + order/cap | D emulated ceiling | rows A / B / Bp |
|---|---|---|---|---|---|
| europe / page25 | 11.1 ms / 1.36 MB | 7.9 ms / 0.86 MB | 8.1 ms / 0.80 MB | 7.7 ms / 0.81 MB | 34135 / 283 / 25 |
| europe / countOnly | 10.7 ms / 1.36 MB | 7.4 ms / 0.79 MB | 6.7 ms / 0.79 MB | 6.6 ms / 0.78 MB | 34135 / 0 / 0 |
| europe / statistics | 10.2 ms / 1.35 MB | 7.2 ms / 0.86 MB | 7.1 ms / 0.86 MB | 7.2 ms / 0.78 MB | 34135 / 0 / 0 |
| global / page25 | 21.1 ms / 2.17 MB | 18.3 ms / 2.05 MB | 8.7 ms / 0.92 MB | 15.7 ms / 1.31 MB | 34135 / 6253 / 25 |
| global / countOnly | 18.6 ms / 2.17 MB | 7.6 ms / 0.91 MB | 7.0 ms / 0.91 MB | 6.6 ms / 0.78 MB | 34135 / 0 / 0 |
| global / statistics | 12.2 ms / 1.83 MB | 11.6 ms / 2.40 MB | 9.1 ms / 2.40 MB | 6.1 ms / 0.79 MB | 34135 / 0 / 0 |

Two findings:

- **"No in-process store accepts an attribute filter at all" is no longer
  true.** `MemoryStore` answers the `FeatureQuery` plan itself and pushes the
  predicate: the harness's probe reports attribute pushdown *supported* on the
  memory store now, and `B` materialises 283 / 6,253 rows for a page rather
  than the whole table. The 2026-09-28 finding was true of the filter-string
  contract that ADR-0074 replaced.
- **A reduction is no longer a read the adapter reduces.** `B`/`Bp` on
  `countOnly` and `statistics` go through `MemoryStore`'s own reduction face
  and materialise **no** `FeatureBatch`, so `rows` is 0 and `countOnly` falls
  from 2.10 MB to 0.91 MB. Grouping every matching row in managed code is
  still not free — `global statistics` allocates 2.40 MB, *above* the emulated
  ceiling's 0.79 MB — but it is the store's reduction, not a page the adapter
  re-reduces.

## End-to-end over HTTP — a host serving PostGIS

`eng/spike-u2x-postgis-e2e.sh`: a throwaway PostGIS container, the snapshot
loaded through the store's own write face, the deployment indexes, a host whose
`postgis` store is that database, and the dataset published through the admin
publish flow (`PUT /api/maps/spike`) as the FeatureServer layer
`world_cities`. Same requests, same layer, 20 iterations, minimum p50 of
`results/idle/e2e-r{1,2,3}.txt`, measured **2026-10-01/02** — so these are the
pre-ADR-0184 figures too. The store half has since been re-measured in process
(the tables above); this HTTP table was not re-run, and its `statistics` route
still carries SpatialEngine-d0q.

| scenario / variant | p50 | p95 | response bytes | rows returned |
|---|---|---|---|---|
| europe / page25 | 230 ms | 364 ms | 4,656 | 25 |
| europe / countOnly | 221 ms | 273 ms | **13** | 283 |
| europe / statistics | 219 ms | 248 ms | 962 | 8 |
| global / page25 | 366 ms | 395 ms | 4,697 | 25 |
| global / countOnly | 348 ms | 363 ms | **14** | 6,253 |
| global / statistics | 415 ms | 428 ms | 12,202 | 171 |

Walking the whole matched set with `resultOffset` at the layer's
`maxRecordCount`:

| scenario | pages | total bytes | total time | mean per page |
|---|---|---|---|---|
| europe (283 matches) | 1 | 46 KB | 233 ms | 233 ms |
| global (6,253 matches) | **7** | **1.01 MB** | 2,624 ms | 375 ms |

**Mirror fidelity: MATCH on both scenarios** — the in-adapter mirror's
`returnCountOnly` answer equals the PostGIS host's, which is served by
`FeatureSpatialMatcher`. So the decomposed `countOnly` numbers above are the
same request the facade runs, on the same store. The `statistics` route is not
the same today: the harness's HTTP request carries no `orderByFields`, and the
served statistics face keeps the match path without one (SpatialEngine-d0q), so
the in-process `B` cell measures the reduction face as the route reaches it when
the group order is stated, not the request as this table sends it.

### The same host shape on the in-memory demo store

So the store half is the only thing that differs. A default host (demo
service, `demo.world_cities` — the same 34,135 rows from the same snapshot),
20 iterations, minimum p50 of `results/idle/e2e-memory-r{1,2,3}.txt`:

| scenario / variant | p50 (memory/demo host) | p50 (PostGIS host) |
|---|---|---|
| europe / page25 | 86 ms | 230 ms |
| europe / countOnly | 85 ms | 221 ms |
| europe / statistics | 85 ms | 219 ms |
| global / page25 | 255 ms | 366 ms |
| global / countOnly | 211 ms | 348 ms |
| global / statistics | 215 ms | 415 ms |
| europe walk (1 page) | 77 ms | 233 ms |
| global walk (7 pages) | 1,678 ms (240 ms/page) | 2,624 ms (375 ms/page) |

**This is the bead's answer.** The 2026-09-28 end-to-end numbers were a Debug
host on the in-memory demo store, so they described a store no deployment uses.
On the store a deployment uses, a 25-row viewport costs **230 ms instead of
86 ms** and a full transfer of 6,253 rows costs **2.6 s instead of 1.7 s** — a
1.6–2.7× penalty for the real store, paid on every request, and the penalty is
in the store read, not in the wire bytes (13 bytes for a 283-match count on
both).

## Appendix: the 2026-09-28 run

Kept for comparison; load 100–120 on 12 cores, run-to-run p50 spread 2–5×, and
path B was a different call. Its PostGIS p50s were A 222 / B 73 / D 7 ms
(europe page25) and A 1,049 / B 170 / D 54 ms (global page25), and its
end-to-end numbers were 155 ms (europe page25) and 1,165 ms for the 7-page
global walk, on the in-memory demo store. Its `rows` column is the measurement
that has held all along; its allocation column has not — the 2026-09-28 run
predates ADR-0185's row-mapping and page-window fixes, so the 31 MB scan and the
46–48 MB whole read are the later figures.

## Caveats and gaps

- The box is shared; every number here is a **minimum of three runs**, and the
  load averages are recorded. Treat the milliseconds as ±20%, not as exact.
- The PostGIS table and the memory table were re-measured on 2026-10-07
  (SpatialEngine-8dm), at `--iterations=10 --warmup=3` and
  `--iterations=30 --warmup=5` respectively. The figures they replace were the
  pre-ADR-0184/ADR-0185 ones: `B`/`Bp` on PostGIS were 46–48 MB per call,
  because every plan was a whole read that mapped each row twice. The reduction
  cells are what ADR-0184 §1 and the harness change moved; the `page25` cells
  moved with ADR-0185's row-mapping and page-window fixes.
- Path `D` remains an emulation over the rows the store holds, and is now the
  reference *beside* a shipped answer for both reduction shapes: path `B` is
  the store's own reduction face (`IFeatureAggregateStore`), which is what
  ADR-0184 §1 pushed on a keyless layer. The two differ only in where the
  reduction runs — `D` over resident features, `B` in SQL for PostGIS — and `B`
  is the faster of the two on PostGIS.
- The world-cities layer declares no identity column, so the per-feature read
  face is unreachable on it on every store (ADR-0140). Path `C` cannot be
  measured here at all.
- The harness lives under `eng/` and is outside the solution. It is no longer
  outside every gate: `tools/spike_harnesses.py` builds each ungated harness in
  every lane (ADR-0190), so it cannot drift out of compilation unnoticed again.
