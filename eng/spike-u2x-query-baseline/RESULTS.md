# Results — SpatialEngine-u2x.1

Re-measured on **2026-10-01/02** (the original run was 2026-09-28; its
load-contaminated figures are kept in the [appendix](#appendix-the-2026-09-28-run)
so the two can be compared).

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
- 30 iterations and 5 warmup for the in-process paths, 20 and 3 over HTTP.
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
  the old `B` cell for cell.
- **`Bp` is new**: the plan a served request actually issues today, with the
  ordering and the row cap in it (ADR-0074 §5, ADR-0116). It is the old `D`
  ceiling as a shipped path, not an emulation, so it is reported separately and
  `D` is kept beside it for continuity.
- `C` is reported as **unavailable** rather than as a number: a dataset that
  declares no identity column is refused with `invalid.arguments` by name
  (ADR-0140), where on 2026-09-28 it answered empty.

## PostGIS, in-process — the store a real deployment uses

p50 ms and MB/call over 30 iterations, indexed table (GiST geometry + btree on
`population` and `country`, `ANALYZE`), minimum p50 of
`results/idle/postgis-r{1,2,3}-indexed.txt`. The plan PostgreSQL chose is in
[`results/idle/postgis-indexed-plan.txt`](results/idle/postgis-indexed-plan.txt)
(a `BitmapAnd` of the GiST scan and the `population` btree).

| scenario / variant | A `ScanAsync`+filter | B plan pushdown | Bp paged plan | D emulated ceiling | rows A / B / Bp |
|---|---|---|---|---|---|
| europe / page25 | 147 ms / 31.11 MB | 179 ms / 46.82 MB | 149 ms / 46.76 MB | 5.8 ms / 0.81 MB | 34135 / 283 / 25 |
| europe / countOnly | 151 ms / 31.10 MB | 162 ms / 46.80 MB | 160 ms / 46.81 MB | 5.9 ms / 0.78 MB | 34135 / 283 / 283 |
| europe / statistics | 159 ms / 31.09 MB | 164 ms / 46.82 MB | 161 ms / 46.78 MB | 6.8 ms / 0.78 MB | 34135 / 283 / 283 |
| global / page25 | 156 ms / 31.91 MB | 180 ms / 48.02 MB | 202 ms / 47.56 MB | 18.1 ms / 1.31 MB | 34135 / 6253 / 25 |
| global / countOnly | 155 ms / 31.91 MB | 158 ms / 48.02 MB | 180 ms / 48.72 MB | 6.5 ms / 0.78 MB | 34135 / 6253 / 6253 |
| global / statistics | 155 ms / 31.58 MB | 147 ms / 47.71 MB | 151 ms / 48.37 MB | 6.1 ms / 0.79 MB | 34135 / 6253 / 6253 |

Repeat spread on the p50: 147–202 ms (europe page25 A), 179–201 ms (europe
page25 B), 149–214 ms (europe page25 Bp).

**What this says, and what it revises.**

1. **The rows argument stands and is stronger than the wall time.** The scan
   builds 34,135 rows and ~31 MB for a 25-row page, every request, and again
   for every page of a walk. The paged plan builds **25 rows** for the same
   request. That is exact and load-independent.
2. **On PostGIS the wall time is now a wash, and that is new.** On 2026-09-28
   pushdown was a 3–6× wall-time win (europe page25 222 → 73 ms). Today A, B
   and Bp all land in the same 150–200 ms band. The database is no longer the
   bottleneck: ~150 ms of every cell is row mapping and `FeatureBatch`
   construction, which the scan also pays. The honest summary is that the
   **materialisation** argument is what the tier-1 work was justified by, and
   the wall-clock argument on this layer no longer supports it on its own.
3. **Allocation does not follow the rows.** `Bp` builds 25 rows and still
   allocates **46–48 MB**, more than the full scan's 31 MB, and the figure does
   not move with the page size. That is unexplained and is not a result this
   spike can call correct; it is filed as its own bead rather than smoothed
   into a conclusion here. (Suspected: work proportional to the match set that
   is not the row mapping itself — the count the plan computes, or the sidecar
   metadata the plan builds before it reads a page.)

## In-memory store (what `eng/seed.sh` and CI use)

Minimum p50 of `results/idle/memory-r{1,2,3}.txt`, 30 iterations.

| scenario / variant | A `ScanAsync`+filter | B plan pushdown | Bp paged plan | D emulated ceiling | rows A / B / Bp |
|---|---|---|---|---|---|
| europe / page25 | 11.8 ms / 1.36 MB | 7.5 ms / 0.86 MB | 7.6 ms / 0.83 MB | 6.6 ms / 0.81 MB | 34135 / 283 / 25 |
| europe / countOnly | 9.4 ms / 1.36 MB | 7.3 ms / 0.86 MB | 7.5 ms / 0.89 MB | 6.6 ms / 0.78 MB | 34135 / 283 / 283 |
| europe / statistics | 9.5 ms / 1.35 MB | 7.2 ms / 0.84 MB | 7.2 ms / 0.88 MB | 6.5 ms / 0.78 MB | 34135 / 283 / 283 |
| global / page25 | 18.2 ms / 2.17 MB | 17.5 ms / 2.10 MB | 16.4 ms / 1.66 MB | 20.3 ms / 1.31 MB | 34135 / 6253 / 25 |
| global / countOnly | 16.3 ms / 2.17 MB | 16.1 ms / 2.10 MB | 22.6 ms / 2.79 MB | 6.4 ms / 0.78 MB | 34135 / 6253 / 6253 |
| global / statistics | 12.0 ms / 1.83 MB | 12.2 ms / 1.77 MB | 22.0 ms / 2.46 MB | 6.8 ms / 0.79 MB | 34135 / 6253 / 6253 |

Two findings, both of which revise the 2026-09-28 story:

- **"No in-process store accepts an attribute filter at all" is no longer
  true.** `MemoryStore` answers the `FeatureQuery` plan itself and pushes the
  predicate: the harness's probe reports attribute pushdown *supported* on the
  memory store now, and `B` materialises 283 / 6,253 rows rather than the whole
  table. The 2026-09-28 finding was true of the filter-string contract that
  ADR-0074 replaced.
- **The paged plan is a loss on the two shapes that reduce.** `Bp` for
  `countOnly` and `statistics` is *unbounded* — the plan has no count and no
  aggregate face yet — so it materialises every matching row and then the
  adapter reduces it, which is slower than the scan (global `countOnly` 16 →
  23 ms, `statistics` 12 → 22 ms). The ceiling those two shapes could reach is
  the D column (6–8 ms), and nothing reaches it today.

## End-to-end over HTTP — a host serving PostGIS

`eng/spike-u2x-postgis-e2e.sh`: a throwaway PostGIS container, the snapshot
loaded through the store's own write face, the deployment indexes, a host whose
`postgis` store is that database, and the dataset published through the admin
publish flow (`PUT /api/maps/spike`) as the FeatureServer layer
`world_cities`. Same requests, same layer, 20 iterations, minimum p50 of
`results/idle/e2e-r{1,2,3}.txt`.

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
`FeatureSpatialMatcher`. So the decomposed path numbers above and the HTTP
numbers below are the same request the facade runs, on the same store.

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
global walk, on the in-memory demo store. The rows and allocation columns there
are the ones that still hold and are reproduced above.

## Caveats and gaps

- The box is shared; every number here is a **minimum of three runs**, and the
  load averages are recorded. Treat the milliseconds as ±20%, not as exact.
- `Bp`'s 46–48 MB per call on PostGIS is unexplained and filed separately.
- Path `D` remains an emulation over the rows the store holds. It is the only
  column that reaches the count and grouped-aggregate shapes, and no store face
  reaches it today (`IFeatureStore` has no count or aggregate plan;
  `IFeatureAggregateStore` is the face that would).
- The world-cities layer declares no identity column, so the per-feature read
  face is unreachable on it on every store (ADR-0140). Path `C` cannot be
  measured here at all.
- The harness lives under `eng/` and is outside the solution and every gate,
  which is how it drifted out of compilation unnoticed. See the bead filed with
  this change.