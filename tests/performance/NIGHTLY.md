# Performance suite D — nightly full job + baselines + docs (T-078, folds T-091)

The nightly job (`.github/workflows/perf-nightly.yml`, 03:23 UTC daily plus
`workflow_dispatch`) runs the measurements that are too slow or too noisy
for a PR: the full BenchmarkDotNet Medium job over every micro, the
sustained-rate host confirmation, and the baseline regression gate. The PR
gate (`eng/verify.sh`, `eng/perf.sh --smoke`) is untouched and stays under
2 minutes: every nightly test skips in the default gate with an explicit
reason.

## What the nightly measures

1. **Full micro job** — `Spatial.Performance` with `--filter '*' --job
   Medium --exporters json` into `artifacts/bench/raw/`. `--job Medium`
   (capital M) is required: bench classes carry `[ShortRunJob]`, so a
   lowercase `--job default` is silently ignored and only the short job
   runs (T-086). With both jobs present the parser keeps the Medium rows.
2. **Sustained-rate confirmation** — `SPATIAL_SUSTAINED=1
   SPATIAL_SUSTAINED_SECONDS=60 dotnet test
   tests/performance/Spatial.HostThroughput.Tests/`: query at 200 rps and
   export at 60 rps, each held for 60 s at a constant offered rate
   (open-loop `PeriodicTimer`, in-process loopback, demo store only).
3. **Baseline gate** — `SPATIAL_PERF_BASELINE=1 dotnet test
   tests/performance/Spatial.Performance.Gates/`: parses the BDN JSON,
   compares against `artifacts/bench/baseline.json` and the committed
   `tests/performance/bench-budgets.json`.

## Budgets

Micro budgets are absolute upper bounds on the full-job (Medium) mean
(source: `BUDGETS.md`, machine copy: `bench-budgets.json`):

| Bench | Budget (mean) |
|---|---|
| Buffer via IGeometryOperations (64-gon) | ≤ 150 µs |
| Intersection via IGeometryOperations (64-gons) | ≤ 180 µs |
| Simplify via IGeometryOperations (512-pt) | ≤ 180 µs |
| Transform point via ProjNetTransforms | ≤ 15 µs |
| Transform 256-pt line via ProjNetTransforms | ≤ 70 µs |
| EsriFeatureCodec encode (point / 128-gon) | ≤ 5 µs / ≤ 150 µs |
| EsriFeatureCodec decode (point / 128-gon) | ≤ 6 µs / ≤ 80 µs |
| MemoryStore scan / bbox (2k points) | ≤ 90 µs / ≤ 80 µs |
| Skia tile 256px (empty / 200 pts) | ≤ 9 ms / ≤ 12 ms |

Host budgets are p95 upper bounds, confirmed both as bursts (smoke) and at
a sustained offered rate (this suite):

| Path | Burst (smoke) | Sustained (60 s) |
|---|---|---|
| FeatureServer query `demo/0?where=1=1` | ≤ 50 ms p95 | ≤ 50 ms p95 at 200 rps |
| PNG export 200×125 circle | ≤ 25 ms p95 | ≤ 25 ms p95 at 60 rps |
| Geometry buffer point+1.0 | ≤ 30 ms p95 | burst-only (not a service path under continuous load) |

All budgets are 🔴 RED/proposed until quiet nightlies confirm them. First
sustained samples (2026-09-14, in-process loopback, shared box): query
p95 0.8 ms at 200 rps, export p95 7.4 ms at 60 rps — steady state sits far
below the burst p95s (query burst p95 16–25 ms), confirming the recorded
burst-tail diagnosis that the slowest ~5% are burst/GC outliers, not
steady-state cost.

## The regression rule

For every bench in `baseline.json`, the gate fails when:

- the current mean regresses **more than 15%** over baseline, or
- allocated bytes per operation grow **more than 15%** over baseline
  (**alloc guard** — fires even when the mean is green), or
- a budgeted bench exceeds its **absolute budget**.

A bench missing from the current run fails (removed or renamed?); a bench
in the run but absent from the baseline passes and is reported as new so
the update flow can absorb it. Raw (`*_Raw`) benches and the Envelope
placeholder carry no budget and are gated on baseline regression only.
`baseline.json` seeds the first nightly (gate skips, update promotes) and
is re-promoted only on green scheduled runs, so the baseline is always the
last green means.

Caveat: GitHub-hosted runners vary in CPU, so a baseline regression near
the 15% bar can be hardware noise — the absolute budget is the stable
signal, the baseline comparison the sensitive one. Re-run via
`workflow_dispatch` before investigating; a persistent red across
dispatches is real.

## Baselines: where they live and how to update

Baselines live in `artifacts/bench/` (gitignored via `artifacts/`):

- `artifacts/bench/baseline.json` — last green means (`{benches: {name:
  {meanNs, allocatedBytes}}}`), restored each nightly from the previous
  green run's `perf-baseline` artifact (90-day retention).
- `artifacts/bench/raw/` — the BDN JSON exports for the current run.
- `perf-results` (30-day retention) is uploaded every run for diagnosis.

Update flows:

- **Automatic** — green scheduled nightlies promote and upload; nothing to do.
- **Manual (local)** — run the full job, then promote locally:
  ```bash
  dotnet run -c Release --project tests/performance/Spatial.Performance \
    -- --filter '*' --job Medium --exporters json --artifacts artifacts/bench/raw
  SPATIAL_BENCH_DIR=artifacts/bench SPATIAL_PERF_BASELINE_UPDATE=1 \
    dotnet test tests/performance/Spatial.Performance.Gates/ -c Release
  ```
  This only affects the local `artifacts/` copy; the shared baseline still
  advances through green nightlies. Never commit `artifacts/`.
- **Check-only (PR)** — `workflow_dispatch` on `perf-nightly.yml` measures
  the branch against the latest baseline without promoting.
- **Budgets** — edit `BUDGETS.md` and `bench-budgets.json` together (the
  gate reads the JSON; reviewers read the markdown) and say so in the commit.

## Files

- `.github/workflows/perf-nightly.yml` — the nightly job.
- `tests/performance/Spatial.Performance.Gates/` — BDN JSON parser,
  comparer (15% + alloc guard + budgets), always-run unit tests, opt-in
  gate/update tests.
- `tests/performance/Spatial.HostThroughput.Tests/SustainedRunner.cs` —
  open-loop constant-rate driver; `HostSustainedRate.cs` — opt-in
  query/export sustained tests; `SustainedRunnerTests.cs` — always-run
  scheduling/percentile/cancellation tests.
- `tests/performance/bench-budgets.json` — committed machine budgets.
- No product code touched; no network in the default gate.
