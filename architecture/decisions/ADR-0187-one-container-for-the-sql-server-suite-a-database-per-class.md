---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: The SQL Server integration suite starts one container for the assembly, shared by a collection, with a database per test class; the start is retried with a generous budget and a skip reason that says whether it was refused or ran out of time.
amends: ADR-0073
related: ADR-0139
---

# ADR-0187: one container for the SQL Server suite, a database per class

## Context

ADR-0073 §7 puts the SQL Server store's integration matrix behind a
Testcontainers container that skips with an explicit reason when the container
is not there. That is honest degradation, and it is exactly what made the
defect SpatialEngine-qhz reports invisible.

Each test class took the container as an `IClassFixture`, and the suite has
twelve container-backed classes. xunit runs test collections in parallel, so a
run of the assembly started twelve SQL Server containers at once. Each image
start pulls, creates, and waits on SQL Server becoming ready — and the wait
strategy Testcontainers ships for `mcr.microsoft.com/mssql/server` gives that
about a minute. Under contention, or on a host another lane is loading, most
of the starts do not finish inside it, and the fixture did what it has always
done: it recorded the failure and every test that needed the container skipped.

The run is green. It is also mostly not a run. Measured in the bead's worktree
on 2026-10-02, the whole assembly reported **22 passed / 119 skipped / 0
failed** in 2 m 19 s with a reachable Docker daemon and every container image
already present, and the skip reasons were `Execution Timeout Expired` and
`The connection is closed`. A standalone run of the class that had skipped all
fifteen of its facts passed fifteen of fifteen, seconds later, with no code
change. ADR-0139 makes a suite in that state a red lane; before this record
the lane was red, and the *branch* was not, because the honest degradation was
doing its job on a defect upstream of it.

A fixture that skips for a reason that clears on its own is not honest, it is
unfalsifiable: the suite's own result cannot tell "the container could not
start" from "these tests are broken", which is the one thing the result is for.

## Decision

**Start one SQL Server container for the assembly and give each test class a
database inside it, retrying a failed start before degrading.**

1. **One container, shared by a collection.** `SqlServerContainerFixture` is
   the fixture of a `[CollectionDefinition]`, and every container-backed class
   joins that collection, so the container is created once per test run
   rather than once per class. The collection sets
   `DisableParallelization = true`: the classes share one SQL Server instance
   and are not run against it concurrently, which costs the suite nothing it
   was not already paying in container starts.

2. **A database per class, not a container per class.** Each class takes
   `SqlServerDatabaseFixture`, a class fixture over the shared container that
   creates a database named from a generated identifier, seeds the spatial
   fixtures, and drops the database when the class is done. The isolation the
   per-class container gave is kept — a class that writes `dbo.places` is not
   fighting the class that asserts on it — while the container is started once.
   The database name is checked against a strict lowercase grammar before it
   is interpolated into `CREATE DATABASE`.

3. **A start is retried, and given a generous budget.** The start goes through
   `SqlServerContainerStart`: up to three attempts, five minutes each, ten
   seconds apart. A start that throws owns whatever it created, so a retry
   leaks no container, and a container whose preparation failed is discarded
   before the next attempt. The whole start is under one linked budget, so an
   attempt that outlives it is recorded as a timeout rather than as a refusal.
   **Cancellation is not degradation**: a cancelled lane propagates its
   `OperationCanceledException` rather than turning into a skip reason.

4. **A skip reason names what happened.** The reason keeps ADR-0073's shape —
   the container could not start, and the Docker daemon is the first thing to
   check — and adds the attempt count and budget, and distinguishes a start
   that was refused from one that ran out of time, because those are different
   facts about the host and a reader of a skip-heavy run needs to tell them
   apart.

5. **The policy is separable from Testcontainers.** The retry, the budget and
   the reason are a static type taking the start as a delegate, so the policy
   is tested as plain facts — a test that needed a container to prove the retry
   could not have failed, which is the defect. A separate fact reads the
   suite's own reflection: every container-backed class takes its fixture from
   the one shared collection, and no class declares the container as its own
   class fixture, so the shape cannot silently regress into a container per
   class.

## Alternatives

- **Leave the degradation and let ADR-0139 catch the runs.** The skip gate
  fails the lane, and the remedy it names is an idle box or
  `--skip-tests`. Rejected: the gate catches a symptom that this suite
  manufactures on an idle box too — the run above was on one — so a branch
  would be red for a reason no amount of waiting on a quieter host reliably
  fixes.
- **Keep a container per class and raise Testcontainers' wait budget.** The
  wait strategy is built inside `MsSqlBuilder` with no public override, and
  twelve concurrent SQL Server instances is the load being complained about;
  a longer wait makes a slow run slower rather than less contended.
- **One container, one shared database, classes in parallel.** Fewer moving
  parts, but the classes write to the same table names (`dbo.places` is
  written by one and asserted on by two others), so it trades a flaky suite
  for a wrong one.
- **One container, one database per class created in parallel.** Isolation
  held, contention partly returned inside a single SQL Server instance, and it
  is the contention that was the defect. The collection runs its classes one
  at a time instead.

## Not decided

- **The PostGIS suite has the same per-class shape and the same 60-second wait
  strategy.** ADR-0189 took this seam: one container for the assembly, a
  database per class, and a retried start. Whether the two suites should share
  one collection of their own is still a separate question, and both records
  leave it open.
- **Whether `tools/skip_gate.py` should read a suite's skip *reasons*.** It
  counts; distinguishing a container that could not start from a suite that
  broke would need the reasons in the trx, which is ADR-0139's own surface.

## Consequences

- The suite's result means what it is supposed to mean: on a reachable daemon
  it runs, and when it does not it says why in words that name the host's
  state. The shape is asserted by facts in the suite and by the source-reading
  guard in `Spatial.Architecture.Tests`, so it stays asserted.
- Twelve container starts become one, so the suite's wall time is roughly what
  it was (measured below) at a fraction of the memory and CPU, and the host
  has one SQL Server to be loaded rather than twelve.
- The classes no longer run in parallel with each other, by design. A suite
  that wanted that parallelism back would need more than one container, which
  is what it had, and the trade is now written down rather than accidental.
- `Spatial.Architecture.Tests`'s container-skip guard reads the fixture type
  out of the suites' sources, so renaming the fixture a class takes silently
  empties what the guard scans. The guard's literal was updated with the
  rename, and the same trap applies to any future one.
- The retry costs a failing run up to three times a start: on a host with no
  daemon at all, the suite now takes minutes to report the same skip it
  reported in one. That is the price of not degrading on a transient, and it
  is paid only on the degraded path.

## References

- ADR-0073 §7 (amended: the containerised matrix is now one container for the
  assembly with a database per class, and a start that is retried), ADR-0139
  (the skip gate this defect reached through)
- `tests/integration/Spatial.SqlServer.Tests/SqlServerContainerFixture.cs`,
  `SqlServerDatabaseFixture.cs`, `SqlServerContainerStart.cs`,
  `SqlServerStartResult.cs`, `SqlServerFixtureSharingTests.cs`,
  `SqlServerContainerStartTests.cs`, the twelve container-backed test classes
- `tests/architecture/Spatial.Architecture.Tests/ContainerSkipGuardTests.cs`
- SpatialEngine-qhz (this record's bead), the run it observed while working
  SpatialEngine-win, and the same shape reported by SpatialEngine-u2x.58

## Measurements

Taken 2026-10-02 in the bead's worktree, `docker info` reachable, the image
already pulled, on the same host for both columns.

| Question | Before (a container per class) | After (one container, a database per class) |
| --- | --- | --- |
| `dotnet test tests/integration/Spatial.SqlServer.Tests` | 22 passed / 119 skipped / 0 failed (plus this bead's 2 red facts) in **2 m 19 s** | 146 passed / 0 skipped / 0 failed in **2 m 22 s** |
| Containers started per run | 12 | 1 |
| What the skipped facts said | `Execution Timeout Expired. The timeout period elapsed…` and `Invalid operation. The connection is closed.` | — |
| `SqlServerContentVersionTests` alone, minutes later | 15 passed / 0 skipped in 10 s (with no code change) | unchanged: it shares the one container |

| Question | Measurement |
| --- | --- |
| Is the wait strategy the thing that gives up? | Yes, and it is not overridable through the builder: `MsSqlBuilder` in Testcontainers 4.14.0 exposes no `WithWaitStrategy` (probed by reflection over the package's public instance members, declared and inherited), so the budget this record raises is the fixture's own, around the whole start, and the retry is what absorbs a 60-second wait strategy losing a race. |
| Does the start policy test need a container? | No — `SqlServerContainerStart` takes the start as a delegate, so all six of its facts and the three sharing facts run in 240 ms with no Docker at all. |
