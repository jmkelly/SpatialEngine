---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: **Cap the host integration suite's xunit collection parallelism at four threads** — `maxParallelThreads: 4` in `tests/integration/Spatial.Host.Tests/xunit.runner.json`, enforced by `TestParallelismTests` — because ADR-0154's one-process cold start is paid once per concurrent collection and the suite was asking 6.8 of the box's 12 cores for its whole run. On an unloaded box the cap costs 19 s of wall clock (2 m 59 s → 3 m 18 s) and takes **37 % of the CPU out of the run** (1203 s summed → 752 s; 83 → 7 results over ten seconds under twelve competing spinners). It is the lever of the three ADR-0154 left open that removes the timeouts; the other two are declined with their measurements.
amends: ADR-0154
---

# ADR-0155: Cap the suite's collection parallelism, and leave the shared host to a later bead

## Context

ADR-0154 measured the host integration suite's tail and stopped one step short
of the fix, on purpose: the first `WebApplicationFactory` boot in a test
process costs 13–15 s of CPU-bound work and every later boot in that process
costs 0.6 s, and xunit parallelises by collection, so on an idle twelve-core
box a dozen collections reach `CreateClient()` at once and that one
process-wide cost is paid concurrently a dozen times. The whole run asks 6.8 of
the box's 12 cores, eight unrelated tests carry a ~20 s floor for no work of
their own, and on a box running three or four swarm lanes the tail runs into
minutes and comes back as the `TaskCanceledException` that had been blocking
beads.

It named three levers — one host per class, a bounded collection parallelism, a
cross-process warm start or ReadyToRun publication — and left the choice to this
record, which needed the experiment: **which lever removes the timeouts.**

## Decision

**Set `maxParallelThreads: 4` for `Spatial.Host.Tests`, in an
`xunit.runner.json` copied to the output directory, and hold it there with a
test.** The other two levers are declined, with their numbers, below.

The cap is a fixed number because `xunit.runner.json` is static JSON with no
expression for the machine it lands on. It reads as a cap on any box, because
xunit clamps a thread count to the cores it has: the same file runs four
collections at a time on twelve cores, two on a four-core laptop and one on a
single-core runner. `SuiteParallelism` holds the policy — a positive count whose
effective width is at most a third of the box's cores, never fewer than two
threads — and `TestParallelismTests` fails the run when the file is missing from
the output directory (which is the silent way this regresses: the runner falls
back to `nproc` and the suite looks configured) or when the value is wider than
the box.

What this buys is CPU, not wall clock, and that is the point: the suite's
wall clock is close enough to unchanged that the merge gate does not notice,
while the box has 37 % more headroom for the lanes around it. The failure this
record exists to remove is one suite starving its neighbours, so total CPU is
the quantity to minimise and wall clock is only the cost.

## Alternatives

- **One host per class rather than per test**, ADR-0154's smallest change. Not
  adopted here, and not because it does not work: it is the smaller *total*
  win. The suite constructs a factory at 204 sites and a warm boot is 0.55 s
  (measured below), so the whole lever is worth about 112 s of a 752 s summed
  run — under a sixth, against ADR-0154's ~30 % for the cap. It is also the
  one that changes what the tests mean: a shared host is shared state, and the
  identity each test publishes under stops being private. `OgcEndpointTests` is
  the cheap instance and it is not cheap — its 53 tests publish a map named
  `world` 47 times, so a class fixture is not a fixture swap, it is a rename
  plus an audit of every test that asserts on a catalog count or a republish.
  Recorded as its own bead rather than smuggled in here.
- **ReadyToRun publication of the host under test, or a cross-process warm
  start.** Not adopted, and not measured here. `DOTNET_ReadyToRun=1` is
  ADR-0154's 12.4 s against 13.3 s for the defaults, because the assemblies are
  not published R2R; supplying them means publishing the test project and its
  reference graph ahead of every run, which is a build-pipeline change and not a
  suite-shape one. A cross-process warm start attacks a cost the cap has already
  taken off the box. Declined for want of a measurement, not on the strength of
  one.
- **Serialise the suite** (`maxParallelThreads: 1`). Rejected as ADR-0154
  rejected it, and the measurement here is sharper: it trades a 20-minute summed
  run for a 20-minute wall clock on a box whose lanes, not the suite, are the
  scarce resource.
- **A cap of six threads.** Measured, because it is the obvious "a bit less
  contention" choice, and it is a third of the benefit: under twelve spinners
  54 results still ran over ten seconds against 7 at four, and the summed time
  was 3695 s against 2313 s. The curve is monotone in the cap, so four is a
  measurement and not a preference.
- **Raise the client timeout further.** Unchanged and still rejected: ADR-0135's
  five minutes is a budget for a hung request, and this record is about not
  needing it.

## Not decided

- **Whether the suite should share one host per class**, and on what identity
  model: a per-test unique dataset name is the honest version, and whether the
  host can hold per-test datasets cheaply is the question that has to be
  measured before sixty classes are converted.
- **What the right cap is on a box that is not this one.** Four is a fixed
  number and the policy accepts anything whose effective width is a third of the
  cores; a machine whose lanes are much wider than its cores — a two-core runner
  in a five-lane swarm — is a shape this record does not model.
- **Whether the cold start can be removed rather than bounded.** 13–15 s is
  still 13–15 s for whichever collection boots first; the cap decides who waits,
  not how long.

## Consequences

- The suite asks about a third of the box instead of two thirds, so three or
  four lanes running it at once stop starving each other — which is the
  condition ADR-0135's five-minute budget was being blamed for.
- The run is about 19 s longer unloaded, and that is the cost: a lane that only
  runs this suite pays it, a lane running four of them pays much less because it
  gets cores back.
- `TestParallelismTests` is a new gate with a failure mode of its own: a
  contributor on a large box who raises the cap "to use the machine" gets a red
  run, which is the intended reading of ADR-0154 rather than an obstacle.
- Nothing about the host's behaviour changed. No contract moved, no assertion
  was relaxed, no test was made to skip, and the two tests ADR-0154 named still
  run against the in-memory store and the demo catalogue with no timeout change.

## References

- ADR-0154 — the measurement this record acts on, and the record it amends.
- ADR-0135 — the five-minute client timeout the load was being blamed for.
- ADR-0109 — the scoped lane whose merge gate this suite sits in.
- `tests/integration/Spatial.Host.Tests/xunit.runner.json` — the cap.
- `tests/integration/Spatial.Host.Tests/SuiteParallelism.cs` — the policy.
- `tests/integration/Spatial.Host.Tests/TestParallelismTests.cs` — the gate:
  the file reached the output directory, the cap is within policy, and a
  cancelled request is still cancellation.
- `tests/integration/Spatial.Host.Tests/Spatial.Host.Tests.csproj` — the
  `None Update` that copies the file.
- Beads: SpatialEngine-cd2 (this record), SpatialEngine-ou1 (ADR-0154's
  measurement), SpatialEngine-c5f (ADR-0135 and the experiment), and the
  follow-up for the shared-host lever.

## Measurements

Host: 12 cores, .NET SDK 10.0.400, `tests/integration/Spatial.Host.Tests` in
Debug, one Testcontainers PostGIS container, 793 passed / 2 skipped in every
run below. `dotnet test --logger "trx;LogFileName=…" --results-directory …`
over `tests/integration/Spatial.Host.Tests/Spatial.Host.Tests.csproj`, read with
`tools/slowest_tests.py`. The wall clock is what `dotnet test` reported; the sum
is the trx duration of every result.

**Unloaded** (`load average` under 5, and the cap run carried the residual load
of the run before it, so it is the conservative direction for the cap):

| Config | Wall | Summed | p50 | p95 | max | >10 s | >20 s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| default (`nproc`) | 2 m 59 s | 1203 s | 1.31 s | 3.78 s | 22.3 s | 7 | 6 |
| **cap 4** | **3 m 18 s** | **752 s** | **0.90 s** | **3.30 s** | 29.7 s | 3 | 1 |
| cap 6 | 3 m 17 s | 1110 s | 1.05 s | 4.50 s | 32.0 s | 5 | 3 |

+19 s of wall for −451 s of CPU. The eight ~20.7 s results that were eight
collections booting at once collapse to one.

**Loaded**, twelve competing spinning processes on the same box (each run
started while the previous run's load was still decaying, so the three are
close to a fair comparison and none of them is a clean one):

| Config | Wall | Summed | p50 | p95 | max | >10 s |
| --- | --- | --- | --- | --- | --- | --- |
| default (`nproc`) | 8 m 08 s | 4013 s | 6.33 s | 11.30 s | 37.2 s | 83 |
| **cap 4** | 11 m 01 s | 2313 s | 3.53 s | 6.29 s | 56.8 s | 7 |
| cap 6 | 12 m 18 s | 3695 s | 5.17 s | 10.41 s | 59.6 s | 54 |

The two tests ADR-0154 named, same runs: `Relate_moves_the_key_and_the_read_follows_it`
9.07 s → 5.08 s and `The_esri_route_serves_a_vector_tile_then_its_cached_bytes`
16.39 s → 17.11 s at twelve spinners; nothing in this table reaches the
five-minute budget, and the >10 s count is the number that was 83.

**Cap 4 at thirty-six spinners** — the deepest load run, and the shape of the
failure ADR-0154 reported: 26 m 53 s wall, 5618 s summed, p50 7.61 s, 217 results
over ten seconds, slowest 2 m 01 s. Green, and two minutes from the budget: a
lane sharing the box with two others has very little headroom left, which is the
argument for the cap rather than against it.

**The warm boot, measured under the final configuration** (a throwaway probe,
run and deleted; not part of the suite): the first `CreateClient()` in a
process was 1 678 ms on a page cache already warm from the runs above, and the
next ten were 871, 595, 525, 505, 652, 567, 553, 506, 451 and 496 ms — a mean of
0.55 s. With 204 factory sites in the suite, that puts the ceiling on the
one-host-per-class lever at about 112 s of summed time, which is the number
that declined it here.
