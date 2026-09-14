---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0063: BenchmarkDotNet for the performance benchmark suite

## Context

The performance work needs a standard .NET microbenchmark harness before the
follow-on slices land: core micros (T-076), host throughput smoke (T-077)
and nightly baselines with docs (T-078). T-067 is the first slice —
scaffolding only, with a fast PR-safe smoke script and one placeholder
bench.

BenchmarkDotNet is the de facto harness: statistically sound jobs,
`MemoryDiagnoser` for allocation tracking, and `--filter`/`--job` flags
that let `eng/perf.sh --smoke` run a single short job in under 2 minutes.
No new product dependency is involved — the package is referenced only by
the benchmark console project under `tests/performance/`.

## Decision

- Add `BenchmarkDotNet` (pinned centrally in `Directory.Packages.props`)
  referenced solely by `tests/performance/Spatial.Performance`.
- The benchmark project is a console `Exe`, not a test project: `dotnet
  test` (and therefore `eng/verify.sh`) builds it but never executes
  benchmarks. The default gate stays fast; benchmarks run only via
  `eng/perf.sh`.
- The bench project references engine projects directly (starting with
  `Spatial.Core` structural values for the placeholder), never via
  `Spatial.PluginSdk` — the SDK takes no packages and carries no third-
  party types (ADR-0005/ADR-0033). NTS, Npgsql and renderer types never
  cross into contracts; benchmarks consume public implementation surfaces
  only.
- No entry in the platform package allowlist
  (`AllowedPackages` in `ArchitectureGuardTests`): the allowlist governs
  `/src` platform projects, and the benchmark project lives under
  `/tests`. `No_inline_package_versions` still applies, hence the central
  pin.

## Consequences

- `eng/perf.sh --smoke` is green in under 2 minutes with the placeholder
  `EnvelopeBenchmarks` bench; `eng/verify.sh` stays green and does not run
  benchmarks.
- Follow-ons (T-076/77/78) add real micros, throughput smoke and nightly
  baselines without revisiting packaging — only new bench classes.
- If a future slice needs a benchmark-only helper package, it gets its own
  ADR and central pin under the same rules.
