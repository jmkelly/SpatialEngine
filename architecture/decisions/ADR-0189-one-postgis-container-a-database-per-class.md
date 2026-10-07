---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: The PostGIS integration suite starts one container for the assembly, shared by a collection, with a database per test class; the start is retried with a generous budget and a skip reason that says whether it was refused or ran out of time.
amends: ADR-0010
amended-by: ADR-0193
related: ADR-0139, ADR-0187
---

# ADR-0189: one container for the PostGIS suite, a database per class

## Context

ADR-0187 §Not decided left this seam open: the PostGIS suite has the same
per-class container shape the SQL Server suite had, and the same 60-second
wait strategy, so the same race. This record takes the seam.

`PostgisContainerFixture` was an `IClassFixture` on every container-backed
class in `tests/integration/Spatial.PostGIS.Tests`, and a run of the assembly
started sixteen PostGIS containers at once. Each image start pulls, creates and
waits on `postgis/postgis:16-3.4` becoming ready, and Testcontainers' wait
strategy for that image gives it about a minute. Under contention most starts
do not finish inside it, and the fixture did what it has always done: it
recorded the failure and every test that needed the container skipped.

The run is green, and it is mostly not a run. ADR-0139 makes that a red lane,
and the deferral is the part worth naming: the SQL Server suite was observed
doing it (22 passed / 119 skipped, `Execution Timeout Expired` and `The
connection is closed`, in SpatialEngine-qhz's worktree on 2026-10-02), and
the reason the PostGIS suite had not been is only that the box had been less
loaded. A defect that is a function of load is a defect that is waiting for a
busy Tuesday.

The failing fact for it is
`PostgisFixtureSharingTests.No_test_class_declares_the_container_as_its_own_class_fixture`,
which named all sixteen classes the day this record was written.

## Decision

**Start one PostGIS container for the assembly and give each test class a
database inside it, retrying a failed start before degrading.** This is
ADR-0187's decision applied to the PostGIS suite, not a second opinion about
it.

1. **One container, shared by a collection.** `PostgisContainerFixture` is the
   fixture of a `[CollectionDefinition]`, and every container-backed class joins
   that collection, so the container is created once per test run rather than
   once per class. The collection sets `DisableParallelization = true`: the
   classes share one PostGIS instance and are not run against it concurrently,
   which costs the suite nothing it was not already paying in container starts.

2. **A database per class, not a container per class.** Each class takes
   `PostgisDatabaseFixture`, a class fixture over the shared container that
   creates a database named from a generated identifier, applies
   `CREATE EXTENSION postgis` and the spatial seed inside it, and drops the
   database when the class is done. The isolation a per-class container gave is
   kept — a class that writes `places` is not fighting the class that asserts
   on it — while the container starts once. The database name is checked
   against a strict lowercase grammar before it is interpolated into
   `CREATE DATABASE`, and the drop is issued `WITH (FORCE)` through the
   container's own administrative connection, because a drop issued through a
   connection *into* the database being dropped cannot finish.

3. **A start is retried, and given a generous budget.** The start goes through
   `PostgisContainerStart`: up to three attempts, five minutes each, ten
   seconds apart — the same shape and the same numbers as
   `SqlServerContainerStart`, which the wait strategy's one minute is the
   direct argument for. A start that throws owns whatever it created, so a
   retry leaks no container. The whole start is under one linked budget, so an
   attempt that outlives it is recorded as a timeout rather than as a refusal,
   and **cancellation is not degradation**: a cancelled lane propagates its
   `OperationCanceledException` rather than turning into a skip reason.

4. **A skip reason names what happened.** The reason keeps the original PostGIS
   shape — the container could not start, and the Docker daemon is the first
   thing to check — and adds the attempt count and the budget, and
   distinguishes a refused start from an out-of-time one.

5. **The policy is separable from Testcontainers, and is the suite's own.**
   `PostgisContainerStart` takes the start as a delegate, so the retry is tested
   as plain facts — a test that needed a container to prove the retry could not
   have failed, which is the defect. It is deliberately a second copy rather
   than a reference to the SQL Server suite's: the two suites are separate test
   assemblies, and one of them should not be able to reach into the other's
   sources. That is a copy of a policy rather than of a fact, and the facts
   that would drift live in the reason strings, which are per store anyway.

6. **The shape stays asserted, and the allocation test keeps its isolation.**
   `PostgisFixtureSharingTests` reads the suite's own reflection: every
   container-backed class takes its fixture from the one shared collection, and
   no class declares the container as its own class fixture, so the shape
   cannot silently regress into a container per class.
   `PostgisKeylessPlanReadAllocationTests` previously declared a collection of
   its own (`postgis-allocation`, `DisableParallelization`) to keep
   `GC.GetTotalAllocatedBytes` a figure of its own; a class may only be in one
   collection, so it joined the shared one instead. That costs it nothing: the
   shared collection also forbids parallelization, which is the property the
   private one existed for, and is in fact a stronger version of it.

## Alternatives

- **Leave the degradation and let ADR-0139 catch the runs.** The gate catches
  the symptom, and the remedy it names is an idle box or `--skip-tests`.
  Rejected, on ADR-0187's reasoning: the gate catches a symptom this suite
  manufactures on an idle box too, so a branch goes red for a reason no amount
  of waiting reliably fixes.
- **Keep a container per class and raise Testcontainers' wait budget.** The
  budget the fixture owns is the one that has to rise either way; sixteen
  concurrent PostGIS instances is the load being complained about, and a longer
  wait makes a contended run slower rather than less contended.
- **One container, one shared database, classes in parallel.** The classes
  write the same table names (`places` is written by several and asserted on by
  more), so it trades a flaky suite for a wrong one.
- **One shared `PostgisContainerStart` in a common test library.** It would
  have removed the copy in decision 5. Rejected as more change than the seam
  needs: a new project referenced by both suites, to serve one static method,
  is a harder thing to change later than the two copies are.
- **Give the two suites one collection together.** ADR-0187 §Not decided
  leaves this open and so does this record; see below.

## Not decided

- **Whether the PostGIS and SQL Server container policies should become one
  type in a shared test library.** The duplication is deliberate and is
  asserted to be small; the question is whether a future third container-backed
  suite makes the copy the wrong shape. What would settle it is a fourth
  copy, or a fix to the policy that has to be made twice.
- **Whether `tools/skip_gate.py` should read a suite's skip *reasons***, which
  is ADR-0187's own open question and is unchanged by this record.

## Consequences

- The suite's result means what it is supposed to mean: on a reachable daemon
  it runs, and when it does not it says why in words that name the host's
  state. Measured below, a full run of the assembly is 148 passed / 0 skipped
  with one container instead of sixteen.
- Sixteen container starts become one, so the host has one PostGIS to be
  loaded rather than sixteen. The suite's wall time goes **up** by about a
  minute and three quarters, because the seed is now paid once per class; see
  Measurements for the numbers and why that trade is the one being made.
- The classes no longer run in parallel with each other, by design — the same
  trade ADR-0187 made and recorded for SQL Server, paid here without the
  measurement that record has.
- Each class now creates and seeds a database, so the seed runs once per class
  rather than once per run. The seed is the same DDL it always was, moved; a
  class that reads `bigpoints` still reads 200,000 rows.
- `Spatial.Architecture.Tests`'s container-skip guard reads the fixture type out
  of the suites' sources, so the literal
  `IClassFixture<PostgisContainerFixture>` was replaced with
  `IClassFixture<PostgisDatabaseFixture>` in it. A future rename of the fixture
  a class takes silently empties what the guard scans — the same trap ADR-0187
  records for the SQL Server side.
- The retry costs a failing run up to three times a start: on a host with no
  daemon at all, the suite now takes minutes to report the same skip it
  reported in one. That is the price of not degrading on a transient, and it
  is paid only on the degraded path.

## References

- ADR-0010 §(containerised PostGIS matrix, amended: one container for the
  assembly, a database per class, and a retried start), ADR-0028,
  ADR-0187 (the SQL Server decision this applies, and whose §Not decided named
  this seam), ADR-0139 (the skip gate this reaches through)
- `tests/integration/Spatial.PostGIS.Tests/PostgisContainerFixture.cs`,
  `PostgisContainerDefinition.cs`, `PostgisDatabaseFixture.cs`,
  `PostgisContainerStart.cs`, `PostgisStartResult.cs`,
  `PostgisFixtureSharingTests.cs`, `PostgisContainerStartTests.cs`, the sixteen
  container-backed test classes
- `tests/architecture/Spatial.Architecture.Tests/ContainerSkipGuardTests.cs`
- SpatialEngine-o5p (this record's bead), SpatialEngine-qhz (the SQL Server
  run whose shape this one had)

## Measurements

Taken 2026-10-02 in the bead's worktree, `docker info` reachable, the
`postgis/postgis:16-3.4` image already pulled, `dotnet test
tests/integration/Spatial.PostGIS.Tests` on the same host for both columns.

| Question | Before (a container per class) | After (one container, a database per class) |
| --- | --- | --- |
| `dotnet test tests/integration/Spatial.PostGIS.Tests` | 139 passed / 0 skipped / 0 failed in **3 m 44 s** | 148 passed / 0 skipped / 0 failed in **5 m 27 s** |
| Containers started per run | 16 | 1 |
| `PostgisFixtureSharingTests` | 1 failed / 1 passed (the 16 classes named) | 3 passed |
| `PostgisContainerStartTests` | (the type did not exist) | 6 passed in ~0.3 s, with no Docker at all |

The extra **1 m 43 s** is the cost decision 6 pays: the seed now runs once per
class rather than once per run, so fifteen class databases are each created,
given the `postgis` extension and filled with 200,000 `bigpoints` rows and then
dropped. It is bought deliberately — a run that costs that much still reports
what it found, which is the property the whole record is about.

