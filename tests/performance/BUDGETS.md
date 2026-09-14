# Performance suite B — micros for hot verbs (T-076)

BenchmarkDotNet micros comparing engine verbs against raw baseline
implementations (raw NTS/ProjNet direct, never live Esri). Every class carries
`[MemoryDiagnoser]` and `[ShortRunJob]`; the project is a console `Exe`, so
`dotnet test` (and `eng/verify.sh`) builds it but never executes benchmarks.

Run (short job, PR-safe, ~3 min):

```bash
dotnet run -c Release --project tests/performance/Spatial.Performance \
  -- --filter "*_*" --job short --artifacts artifacts/benchmarks
```

Single verb, e.g. `--filter "*BufferBenchmarks*"`. `eng/perf.sh --smoke` still
runs the T-067 `EnvelopeBenchmarks` placeholder only; promoting a `--micros`
mode is a T-077/T-078 follow-up.

## Test-first budgets: RED

Budgets below are **proposed upper bounds on the full-job mean**, recorded
red before any optimisation (this slice must NOT optimise product code to hit
budgets — record deltas, file follow-ups). Short-run numbers are noisy
single-launch samples from 2026-09-14 (Xeon E5-2697 v2, .NET 10); a full
nightly run confirms or revises them.

| Bench | Short-run observed | Proposed full-job budget | Status |
|---|---|---|---|
| Buffer via IGeometryOperations (64-gon) | 80 µs (raw 84 µs, ratio 0.96) | ≤ 150 µs | 🔴 RED |
| Intersection via IGeometryOperations (64-gons) | 106 µs (raw 77 µs, ratio 1.37) | ≤ 180 µs | 🔴 RED |
| Simplify via IGeometryOperations (512-pt) | 103 µs (raw 84 µs, ratio 1.23) | ≤ 180 µs | 🔴 RED |
| Transform point via ProjNetTransforms | 8.1 µs (raw 0.21 µs, ratio **38.7**) | ≤ 15 µs | 🔴 RED |
| Transform 256-pt line via ProjNetTransforms | 35.7 µs (raw 32.3 µs, ratio 1.12) | ≤ 70 µs | 🔴 RED |
| EsriFeatureCodec encode (point / 128-gon) | 2.1 µs / 95 µs | ≤ 5 µs / ≤ 150 µs | 🔴 RED |
| EsriFeatureCodec decode (point / 128-gon) | 2.6 µs / 40 µs | ≤ 6 µs / ≤ 80 µs | 🔴 RED |
| MemoryStore scan / bbox (2k points) | 44 µs / 39 µs | ≤ 90 µs / ≤ 80 µs | 🔴 RED |
| Skia tile 256px (empty / 200 pts) | 5.0 ms / 7.1 ms | ≤ 9 ms / ≤ 12 ms | 🔴 RED |

## Recorded deltas (not optimised in this slice)

- **Transform point 38.7× vs raw** (8 060 ns vs 209 ns, +7.7 KB alloc):
  per-call CRS lookup + stamp validation dominates a single-point transform;
  amortised away on the 256-pt line (1.12×). Follow-up: cache the resolved
  `MathTransform` per (source, target) or document single-point cost.
- **Intersection 1.37× / Simplify 1.23× vs raw**: the Core↔NTS adapter +
  validation share; Buffer is at parity (0.96×, within noise).
- **Tile +2.1 ms for 200 points** (~10.7 µs/feature over the 5.0 ms fixed
  pipeline cost).
- **FeatureQueryEngine is internal** to `Spatial.Adapter.GeoServices`, so the
  query micro pins the store verbs underneath it (`MemoryStore` scan/bbox);
  the engine fan-out itself stays covered by the adapter unit suite.

## Full-job confirmation (T-086, 2026-09-14)

Medium-job rerun of all 19 micros on the same CPU family as the short-run
samples (Xeon E5-2697 v2, 24 threads, .NET 10.0.12, BDN 0.15.7):

```bash
./tests/performance/Spatial.Performance/bin/Release/net10.0/Spatial.Performance \
  --filter '*' --job Medium --artifacts artifacts/benchmarks
```

Every budgeted mean reproduces within its proposed upper bound, so budgets
and 🔴 statuses are unchanged (no retuning in this slice). Means below are
the `MediumRun` rows (15 iterations × 2 launches); the `ShortRun` rows from
the same launch agree to within noise.

| Bench | Medium-run mean | Budget | Headroom |
|---|---|---|---|
| Buffer via IGeometryOperations (64-gon) | 62.1 µs (raw 56.5 µs, ratio 1.10) | ≤ 150 µs | 2.4× |
| Intersection via IGeometryOperations (64-gons) | 76.8 µs (raw 73.5 µs, ratio 1.05) | ≤ 180 µs | 2.3× |
| Simplify via IGeometryOperations (512-pt) | 126.6 µs (raw 81.0 µs, ratio 1.56) | ≤ 180 µs | 1.4× |
| Transform point via ProjNetTransforms | 6.77 µs (raw 0.19 µs, ratio 35.8) | ≤ 15 µs | 2.2× |
| Transform 256-pt line via ProjNetTransforms | 35.0 µs (raw 22.6 µs, ratio 1.55) | ≤ 70 µs | 2.0× |
| EsriFeatureCodec encode (point / 128-gon) | 0.92 µs / 52.0 µs | ≤ 5 µs / ≤ 150 µs | 5.4× / 2.9× |
| EsriFeatureCodec decode (point / 128-gon) | 1.22 µs / 33.7 µs | ≤ 6 µs / ≤ 80 µs | 4.9× / 2.4× |
| MemoryStore scan / bbox (2k points) | 39.4 µs / 38.9 µs | ≤ 90 µs / ≤ 80 µs | 2.3× / 2.1× |
| Skia tile 256px (empty / 200 pts) | 4.83 ms / 7.01 ms | ≤ 9 ms / ≤ 12 ms | 1.9× / 1.7× |

Notes:

- **Simplify mean 126.6 µs vs median 99.0 µs** (StdDev ~32 µs): a few slow
  iterations inflate the mean; still 1.4× under budget. If the nightly
  tightens budgets, prefer the median or investigate the outliers first.
- **Transform point ratio 35.8×** reproduces the recorded 38.7× delta
  (per-call CRS lookup + stamp validation vs cached raw `MathTransform`).
- **CLI gotcha (BDN 0.15.7)**: `--job default` (lowercase) is silently
  ignored when bench classes carry `[ShortRunJob]` — only the short job
  runs. `--job Medium` (capital M) overrides the attribute and runs the
  full job; with both present the jobs union (this confirmation ran
  19 Medium + 18 Short cases; `EnvelopeBenchmarks` has no job attribute
  so it ran Medium only). The nightly driver should use `--job Medium`.
