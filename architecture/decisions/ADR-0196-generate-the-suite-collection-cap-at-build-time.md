---
status: accepted
date: 2026-10-08
deciders: maintainer + agent
summary: The host integration suite's xunit collection cap is **generated at build time** from the building machine's core count — `max(2, nproc/3)` written into `xunit.runner.json` beside the test assembly — instead of a shipped fixed 4, because a fixed number cannot sit below nproc on every box and the shipped 4 fails the suite's own ceiling on anything under twelve cores.
amends: ADR-0155
---

# ADR-0196: Generate the suite's collection cap at build time

## Context

ADR-0155 capped `Spatial.Host.Tests` at `maxParallelThreads: 4` in a shipped
`xunit.runner.json`, held there by `TestParallelismTests`. The cap was a fixed
number because the file is static JSON, and the record claimed it reads as a
cap on any box since xunit clamps to the cores it has.

That claim is wrong on its own terms. `SuiteParallelism.IsBounded` accepts a
count whose effective width is at most `Ceiling(cores) = max(2, cores/3)`, and
the live test reads the file against the box it runs on — so the shipped 4
passes on twelve cores and fails on four, where the ceiling is 2. CI's
integration job runs on four-core runners and has been red on
`The_runner_configuration_reached_the_output_directory` since the cap landed:
a gate that only passes on the box the number was measured on.

## Decision

**Write `xunit.runner.json` at build time, with the building machine's own
ceiling.** `Spatial.Host.Tests.csproj` carries a `GenerateXunitRunnerConfig`
target that computes `max(2, nproc/3)` from `System.Environment.ProcessorCount`
with MSBuild property functions and writes the file beside the test assembly
before build, preserving the other runner keys. No static file is shipped, and
`TestParallelismTests` gains the pin: the generated number equals
`SuiteParallelism.Ceiling` for the box the suite runs on.

## Alternatives

Ship a fixed 2. Passes on every box, but narrows twelve-core runs below the
knee ADR-0155 measured, and the number would still be wrong somewhere the
moment the fleet changes shape. Rejected: it re-buys the same defect one
fleet change later.

Exempt small boxes in the test. Keeps the shipped behaviour and turns the
gate vacuous exactly where oversubscription hurts most — on a four-core box
an effective width of 4 *is* xunit's default, the shape the gate exists to
forbid. Rejected: it blesses the forbidden shape.

## Consequences

The suite's parallelism is correct on any box by construction, and the gate
keeps its teeth everywhere instead of only on large machines. The cost is a
build that reads its own machine: a test assembly copied to another box
without rebuilding carries the wrong cap, and a stale output directory
without a rebuild runs yesterday's number — both are the normal meaning of a
build product, and the new pin fails loudly if the two ever disagree.

## References

ADR-0155 (amended: the fixed cap), ADR-0154 (the cold-start measurement the
cap exists for). Files: `tests/integration/Spatial.Host.Tests/
Spatial.Host.Tests.csproj` (the `GenerateXunitRunnerConfig` target),
`SuiteParallelism.cs` (remarks corrected), `TestParallelismTests.cs` (the
generator-policy pin). Bead: SpatialEngine-esd.

## Measurements

What was measured, how, and what the numbers were.

| date | tool | box | cap written | `IsBounded` |
| ---- | ---- | --- | ----------- | ----------- |
| 2026-10-08 | `dotnet build` + `dotnet test --filter TestParallelismTests` | 12 cores (dev) | 4 | pass (4 ≤ 4) |
| 2026-10-08 | `dotnet build -p:_HostTestCores=4`, policy mirror in Python | simulated 4 cores | 2 | pass (2 ≤ 2) |
| 2026-10-08 | GH run 37738664260, integration job | 4 cores (runner) | 4 (static, before) | fail (4 > 2) |
