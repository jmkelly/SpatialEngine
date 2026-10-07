# Idle re-measurement — load average and command for every run

Taken on 2026-10-01/02 AEST on the same 12-core Linux box the 2026-09-28 spike
ran on (.NET 10, PostgreSQL 16 / PostGIS 3.4 in Docker). The box is shared with
a parallel agent swarm, so it was never truly idle; what changed is that the
estimator is the **minimum p50 of three repeats** rather than the p50 of one
run, which is robust to a neighbour's load (contention only ever adds time).

`load1/load5/load15` are the kernel's load averages read immediately before each
run started.

| file | command | load before |
|---|---|---|
| `memory-r{1,2,3}.txt` | `eng/spike-u2x-query-baseline.sh --store=memory --iterations=30 --warmup=5` | 8.79 / 33.26 / 28.23, then 5.97 / 28.34 / 26.84, then 5.45 / 24.07 / 25.46 |
| `postgis-r1-indexed.txt` | `SKIP_TEARDOWN=1 eng/spike-u2x-query-baseline.sh --store=postgis --iterations=30 --warmup=5` (loads the table, indexes it, measures both phases) | 4.04 / 19.42 / 23.72 |
| `postgis-r{2,3}-indexed.txt` | `dotnet run -c Release --project eng/spike-u2x-query-baseline -- --store=postgis --connection=… --reuse --iterations=30 --warmup=5` | 2.39 / 6.94 / 16.01, then 5.18 / 6.16 / 13.97 |
| `e2e-r{1,2,3}.txt` | `eng/spike-u2x-postgis-e2e.sh --iterations=20 --warmup=3` (throwaway PostGIS + a host serving it + the dataset published as a FeatureServer) | 12.42 / 37.90 / 33.44, then 2.44 / 19.19 / 26.82 |
| `e2e-memory-r{1,2,3}.txt` | the same harness with `--store=memory --host=…` against a default host serving the `demo` service (`demo.world_cities`, the same 34,135 rows) | 1.39 / 6.04 / 17.38, then 1.64 / 5.27 / 16.40, then 2.85 / 5.00 / 15.56 |
| `postgis-r1-noindex.txt` | the no-index phase of the `postgis-r1` run | as above |

`postgis-indexed-plan.txt` is the plan PostgreSQL chose over the indexed table
for the europe request (a `BitmapAnd` of the GiST and the `population` btree).

## 2026-10-07 re-run — the reduction cells (SpatialEngine-8dm)

Re-measured because the harness now routes a reduction through the store's own
reduction face (`IFeatureAggregateStore`) rather than reducing a `QueryAsync`
read in the adapter, so the `countOnly` and `statistics` cells moved. Same box,
same estimator (minimum p50 of three repeats). The PostGIS runs use
`--iterations=10 --warmup=3` (the command the bead names) and the memory runs
keep `--iterations=30 --warmup=5`.

| file | command | load1 before |
|---|---|---|
| `postgis-0184-r1-indexed.txt` | `SKIP_TEARDOWN=1 eng/spike-u2x-query-baseline.sh --store=postgis --iterations=10 --warmup=3` (loads the table, indexes it, measures both phases) | 5.01 |
| `postgis-0184-r{2,3}-indexed.txt` | `dotnet run -c Release --project eng/spike-u2x-query-baseline -- --store=postgis --connection=… --reuse --iterations=10 --warmup=3` | 9.41, then 5.55 |
| `memory-0184-r{1,2,3}.txt` | `eng/spike-u2x-query-baseline.sh --store=memory --iterations=30 --warmup=5` | 5.81, then 6.05, then 4.73 |
| `postgis-0184-r1-noindex.txt` | the no-index phase of the `postgis-0184-r1` run | as above |

`postgis-0184-r1-indexed-plan.txt` is that run's indexed-table plan, the same
`BitmapAnd` as the 2026-10-02 run.
