# Performance suite C — host throughput smoke + p95 budgets (T-077)

In-process host smoke asserting p95 latency budgets for the three hot host
paths, driven over loopback `HttpClient` via `WebApplicationFactory` (no
network, demo store only). The BenchmarkDotNet micros (suite B) pin
per-verb cost; this slice pins host-edge cost including routing, the Esri
wire codec and JSON serialisation.

Run (opt-in, ~6 s, PR-safe):

```bash
SPATIAL_THROUGHPUT=1 dotnet test tests/performance/Spatial.HostThroughput.Tests/ -c Release
```

Without `SPATIAL_THROUGHPUT=1` every test skips with an explicit reason, so
the default gate (`eng/verify.sh`, `dotnet test SpatialEngine.slnx`) stays
fast and green — the smoke is `ShortRun`-only and excluded from the default
gate exactly like the BenchmarkDotNet slice, which builds but never executes
under `dotnet test`. The project is a solution member (the architecture
`Solution_membership_is_exact` guard requires every project under `/tests`
to be in `SpatialEngine.slnx`).

## Method

- Warmup burst mirrors the measured burst (JIT + query/render plan +
  threadpool ramp outside the window), then a fixed measured window:
  query 300× at concurrency 16, buffer 300× at concurrency 16
  (typed `SpatialClient`), export 100× at concurrency 4
  (`POST /api/render`, 200×125 PNG circle over `demo.cities`).
- Per-request latency via `Stopwatch` around `Parallel.ForEachAsync`;
  p50/p95/max + achieved rps go to the test log. One overall
  `CancellationTokenSource` (100 s) bounds each window — long work stays a
  cancellable `Task`, and host failures surface as the structured
  `SpatialException` codes (`invalid.arguments`, `not.found`,
  `store.unavailable`), asserted via the smoke's status checks.
- "100–1000 rps" is the offered-load band: the smoke offers concurrency 16
  (query/buffer) and records achieved rps (5–9 k for query/buffer, ~740 for
  export) without gating on it — rps is machine- and payload-dependent, so
  only the p95 latency is budgeted.

## Test-first budgets: RED

Budgets below are **proposed p95 upper bounds**, recorded red before any
optimisation (this slice must NOT optimise product code to hit budgets).
Short-run numbers are noisy single-machine samples from 2026-09-14 (.NET 10,
in-process loopback); a full nightly run confirms or revises them. The RED
pass ran with 2 ms / 10 ms / 2 ms placeholders and failed on all three
paths as required before budgets were set:

| Smoke (measured window) | Short-run observed | Proposed p95 budget | Status |
|---|---|---|---|
| FeatureServer query `demo/0?where=1=1` (300×, c=16) | p50 ~1 ms, p95 16–25 ms, ~6 k rps | ≤ 50 ms | 🔴 RED |
| PNG export 200×125 circle (100×, c=4) | p50 ~5 ms, p95 ~8 ms, ~700 rps | ≤ 25 ms | 🔴 RED |
| Geometry buffer point+1.0 (300×, c=16) | p50 ~1 ms, p95 4–22 ms, ~7 k rps | ≤ 30 ms | 🔴 RED |
| Live Esri `sampleserver6` services root | ~1.2 s (200 OK) | none — reference only, never gated | ⚪ record |

## Recorded deltas (not optimised in this slice)

- **Burst tail dominates p95** (p50 ~1 ms vs p95 16–25 ms on query;
  buffer p95 swung 3.5 → 21.7 ms run-to-run on the same box): the slowest
  ~5 % are burst/GC outliers, not steady-state cost. A sustained-rate
  driver (constant offered rps with a latency histogram, e.g. HdrHistogram)
  belongs in the nightly suite; the smoke keeps the simple burst shape.
- **Shared-box contention moves p95**: an export window spiked to p95
  38.6 ms (3+ stalled renders) while a neighbouring worktree ran its own
  suite; the same config sits at p95 ~8 ms alone. Mitigation in the
  smoke: export runs n=100 so p95 tolerates a handful of stalled samples,
  and every window logs p99 + the over-budget sample count for diagnosis.
  Budgets stay RED/proposed until the quiet nightly (T-091) confirms them.
- **Live Esri ~1.2 s** from this box vs sub-millisecond in-process p50s:
  three orders of magnitude of headroom for offline-first design; recorded
  only, no budget, skips (never fails) when egress is unavailable.
  Follow-ups: T-091 (nightly sustained-rate driver + budget confirmation),
  T-092 (query burst-tail diagnosis). No product code touched.

## Sustained confirmation (T-078, folds T-091)

The burst shape above is complemented by the suite-D sustained driver
(`SustainedRunner` + `HostSustainedRate`, see `NIGHTLY.md`): query at
200 rps and export at 60 rps held for 60 s at a constant offered rate,
asserting the same p95 budgets under continuous load. First samples:
query p95 0.8 ms, export p95 7.4 ms — steady state far below the burst
p95s, supporting the burst-tail diagnosis. Buffer stays burst-only.
