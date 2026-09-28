# Results — SpatialEngine-u2x.1

The number the Tier 1 branch of `SpatialEngine-u2x` is justified by. Run on
2026-09-28, 12-core Linux box, .NET 10, PostgreSQL 16 / PostGIS 3.4 in Docker.
Layer: the committed GeoNames world-cities snapshot, **34,135 rows**
(`demo.world_cities`, CC-BY 4.0), loaded through the store's own create/write
faces into `public.spike_world_cities`.

Request in every cell: `where=population > 100000`, an
`esriGeometryEnvelope` bbox, `orderByFields=population DESC`,
`resultRecordCount=25` (`page25`), plus `returnCountOnly` (`countOnly`) and a
grouped `outStatistics` (count + avg population, `groupBy=country`). Two bboxes:
`europe` = −10,36,5,55 (**283** matches) and `global` = the world
(**6,253** matches).

**Caveat on wall time.** The measurement box was shared with a parallel agent
swarm: load average was 100–120 on 12 cores throughout. Run-to-run p50 for the
same path varied by **2–5×**. The `rows` and `result` columns are exact and
identical in every run; the wall-time columns are not, and the in-process
allocation figures move by up to ~30% run to run (JIT tiering inside the
measured window) while the PostGIS ones — which are dominated by row mapping —
are stable to the last decimal. Ratios below use the least-contended p50 of the
four runs; the recorded run's raw output is committed under `results/`.

## Headline — PostGIS, the store a real deployment uses

p50 ms, least-contended run, 30 iterations (`results/postgis-*.txt` has the
recorded run; these p50s come from the quietest of the four).

| scenario / variant | A `ScanAsync`+filter | B `QueryAsync` pushdown | D emulated full pushdown | C by identity |
|---|---|---|---|---|
| europe / page25 | **222 ms** | 73 ms | 7 ms | 36 ms (0 rows) |
| europe / countOnly | **362 ms** | 66 ms | 6 ms | n/a |
| europe / statistics | **444 ms** | 84 ms | 28 ms | n/a |
| global / page25 | **1,049 ms** | 170 ms | 54 ms | 113 ms (0 rows) |
| global / countOnly | **279 ms** | 121 ms | 4 ms | n/a |
| global / statistics | **368 ms** | 125 ms | 9 ms | n/a |

Exact, load-independent columns (identical in every run):

| path | rows materialised | managed alloc / call | europe result | global result |
|---|---|---|---|---|
| A `ScanAsync` + in-adapter filter | **34,135** (every row, every request) | **30.3–31.1 MB** (stable to the decimal across runs) | 25 / 283 / 8 | 25 / 6,253 / 171 |
| B `QueryAsync` (bbox + where pushdown) | 283 (europe) / 6,253 (global) | 0.31 MB / 6.35 MB | 25 / 283 / 8 | 25 / 6,253 / 171 |
| D emulated full pushdown | 25 (page) / 0 (count) / 8–171 (groups) | 0.00–0.53 MB | 25 / 283 / 8 | 25 / 6,253 / 171 |
| C `IFeatureLookup.GetAsync` | **0** | 0.04–0.55 MB | 0 | 0 |

**The premise holds, and the ordering is A ≫ B > D in every single run:**

1. **Filter pushdown is a 3–6× win** (europe page25 222 → 73 ms; europe
   statistics 444 → 84 ms; global page25 1,049 → 170 ms) and a **10–100×
   allocation win** (30.3 MB → 0.31 MB for europe). `IFeatureStore.QueryAsync`
   with the equivalent filter is genuinely faster, not a wash.
2. **Pushdown is not the whole prize.** Going from B to D — pushing ordering,
   `offset`/`limit` and aggregation into the store so only the rows the
   response needs are materialised — is a further **2–13×** (europe page25
   73 → 7 ms; global countOnly 121 → 4 ms). B still materialises every matching
   row into `FeatureBatch` pages, exactly as the bead suspected. The Tier 1
   store-query surface (`.9`) is worth more than the filter pushdown alone.
3. **The production path's cost is the materialisation, and it is constant.**
   A materialises 34,135 rows and ~30 MB whether the request is a 283-row
   viewport or the whole world, and it does it again for *every page* of a
   paged walk. `outStatistics` and `returnCountOnly` are the worst-affected
   shapes: they throw away almost everything they built.

## In-memory store (what `eng/seed.sh` and CI use)

`results/memory.txt` (30 iterations, recorded run under load; the p50 below
is the least-contended run, the allocations are the recorded run's). The
in-process store is a much weaker case for the contract work, and the reason is
structural:

| scenario / variant | A `ScanAsync`+filter | B `QueryAsync` pushdown | D emulated full pushdown | C by identity |
|---|---|---|---|---|
| europe / page25 | 22 ms / 0.58 MB | 12 ms / 0.14 MB | 17 ms / 0.03 MB | 17 ms / 0.03 MB |
| europe / countOnly | 22 ms / 0.58 MB | 13 ms / 0.11 MB | 12 ms / 0.00 MB | n/a |
| europe / statistics | 16 ms / 0.57 MB | 14 ms / 0.12 MB | 11 ms / 0.00 MB | n/a |
| global / page25 | 33 ms / 1.38 MB | **42 ms / 2.15 MB** | 22 ms / 0.53 MB | 22 ms / 0.54 MB |
| global / countOnly | 18 ms / 1.38 MB | **38 ms / 2.14 MB** | 8 ms / 0.00 MB | n/a |
| global / statistics | 20 ms / 1.06 MB | **24 ms / 1.60 MB** | 7 ms / 0.01 MB | n/a |

Two findings the epic's framing does not assume:

- **No in-process store accepts an attribute filter at all.** Both
  `MemoryStore.QueryAsync` and `DemoStore.QueryAsync` throw
  `invalid.arguments` for any `filter` and push the bbox only (the harness
  probes this and labels the column `B-bbox`). So the `where` in a
  FeatureService query is *always* evaluated in the adapter, on every store
  that is not PostGIS or SQL Server.
- **Where the pushdown cannot filter, it is a loss.** The global bbox matches
  every row, so the "pushdown" is pure overhead: 33 ms → 42 ms and
  0.58 → 2.15 MB, because the store re-walks and re-copies 34,135 rows and the
  adapter then filters them anyway. A pushdown contract must be able to say
  "not worth it" or push nothing.

## End-to-end bytes and paging (`results/memory-with-http.txt`)

Real HTTP against a running host's demo FeatureServer (layer `world_cities`),
20 iterations, quietest run:

| scenario / variant | p50 | response bytes | rows returned |
|---|---|---|---|
| europe / page25 | 155 ms | 4,661 | 25 |
| europe / countOnly | 133 ms | **13** | 283 |
| europe / statistics | 200 ms | 962 | 8 |
| global / page25 | 198 ms | 4,702 | 25 |
| global / countOnly | 217 ms | **14** | 6,253 |
| global / statistics | 246 ms | 12,202 | 171 |

Walking the whole matched set with `resultOffset` at the layer's
`maxRecordCount`:

| scenario | pages | total bytes | total time | mean per page |
|---|---|---|---|---|
| europe (283 matches) | 1 | 46 KB | 92 ms | 92 ms |
| global (6,253 matches) | **7** | **1.01 MB** | 1,165 ms | 166 ms |

So a client retrieving 6,253 rows pays **7 full scan-and-materialise cycles**
and moves ~1 MB. The wire bytes are small; the waste is on the server side, once
per page, and only a store that can page (`D`) removes it.

**Mirror fidelity: MATCH on both scenarios.** The in-adapter mirror's
`returnCountOnly` answer equals the real host's, which is served by
`FeatureSpatialMatcher` — so the decomposed path-A/B numbers are the same
request the facade actually runs.

## Caveats and gaps

- Wall times are load-contaminated (see above). Re-run
  `eng/spike-u2x-query-baseline.sh` on an idle box before quoting absolute
  milliseconds; the ratios and the rows/allocation columns will not move.
- The end-to-end HTTP numbers are a **Debug**-built host against the in-memory
  demo store. A host serving PostGIS was not measured end-to-end; the store
  half of that is what the PostGIS table above covers.
- The PostGIS table was measured twice: as `PostgisStore.CreateAsync` leaves it
  (no indexes, `Seq Scan`) and after adding the GiST geometry index, a btree on
  `population` and one on `country` plus `ANALYZE`
  (`BitmapAnd` of two index scans → 283 rows). Pushdown p50 improves 73 → 44 ms
  (europe) and 170 → 109 ms (global) with the indexes; scan path A is
  unchanged, because A never benefits from an index when it reads everything.
  Plans are committed under `results/postgis-*-plan.txt`.
- Path D is an **emulation** over the rows the store holds, not a store call.
  It is deliberately two-sided: it shows the materialisation and allocation a
  store-side pushdown would save, while still paying the in-process predicate
  scan that a real database does with an index. A real store-side pushdown
  would be at least as good.
- Path C returns **0 rows** on both stores: `PostgisFeatures.ByIdentityAsync`
  returns empty when the dataset declares no identity column, and
  `MemoryStore.GetAsync` resolves ids. The world-cities layer has no integer
  identity column, so on this layer the per-feature read face is unreachable —
  the Esri `OBJECTID` is a scan ordinal, which is not a durable key.
