---
status: accepted
date: 2026-10-07
deciders: maintainer + agent
summary: The container fixtures refuse a connection string while Docker is unavailable, so an unguarded fact fails loudly; the source-reading guard becomes a backstop that fails when its fixture literal stops matching.
amends: ADR-0187, ADR-0189
related: ADR-0139
---

# ADR-0193: the container fixtures refuse an unguarded access, and the skip guard fails when it matches nothing

## Context

ADR-0187 and ADR-0189 made the two containerised suites' shape honest: one
container for the assembly, a database per class, a retried start, and a skip
reason that says whether the start was refused or ran out of time. Their
Consequences sections named the enforcement that came with it —
`ContainerSkipGuardTests` in `Spatial.Architecture.Tests`, landed in
SpatialEngine-8af and SpatialEngine-win. It reads every `[SkippableFact]` and
`[SkippableTheory]` in a class that takes the database fixture and asserts the
fact consults `_fixture.DockerAvailable` before it reaches the store.

The invariant is right, and the enforcement is textual. Two ways that fails,
one noisy and one silent. A reformat, a rename of the fixture, or a different
skip idiom makes the scan miss facts and the guard goes red for a reason that
is not a defect. Worse, the scan is driven by a literal — the fixture string a
class is looked for — so a literal that stops matching makes `ContainerBackedClasses`
return nothing, `Assert.Empty` passes vacuously, and the suite stays green while
the invariant it was written to hold is no longer held. ADR-0187 §Consequences
records exactly that trap after the PostGIS rename ("renaming the fixture a
class takes silently empties what the guard scans"), and ADR-0189 repeats it.
A guard whose failure mode is *becoming green* is not a guard.

The container-shape fix does not close this. A container that will not start
still yields a green, mostly-skipped lane, and the guard is the only thing
auditing whether the facts that met that lane were entitled to skip.

## Decision

**The database fixture refuses a connection-string access while the container
is not available, so an unguarded fact fails; the source scan stays as a
backstop that fails when it matches fewer classes than the suite has.**

1. **The refusal is structural.** `PostgisDatabaseFixture.ConnectionString` and
   `SqlServerDatabaseFixture.ConnectionString` throw `InvalidOperationException`
   while `DockerAvailable` is false, with a message naming the guard the fact
   forgot — `Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no
   reason")` — and the recorded skip reason. The setter is private and used only
   by `InitializeAsync`, so the fixture still sets the string before it turns
   `DockerAvailable` true. A fact that reads the connection string unguarded now
   fails at its first access with the fix in the message, rather than opening a
   connection to an empty string or, on a host that happens to have the image,
   passing having tested nothing.

2. **The refusal is asserted, without Docker.** `PostgisFixtureRefusalTests`
   and `SqlServerFixtureRefusalTests` construct an uninitialized container
   fixture — `DockerAvailable` false, no container started — and assert the
   property throws. This is the guard: delete the throw and the fact fails. It
   needs no daemon, so it runs wherever the suite is built.

3. **The source scan is a backstop, and it fails loudly on vacuity.**
   `ContainerSkipGuardTests` keeps reading the suites' sources, because it is
   the only check that runs on a lane that never starts a container. Its
   `ContainerBackedClasses` now refuses to return a scan that matched fewer than
   the suite's floor of container-backed classes (`ContainerBackedFloor`, 10),
   throwing a message that names the drifted literal. A fact asserts that
   refusal, so the backstop's own failure mode is covered.

4. **The two suites keep their own copy.** As in ADR-0189 decision 5, the two
   container policies are deliberately separate assemblies, and one should not
   reach into the other's sources. The refusal is a handful of lines per
   fixture, and the message names the fixture it came from.

5. **`tools/skip_gate.py` is out of scope.** ADR-0187 §Not decided left open
   whether the gate should read skip *reasons* rather than counts; this record
   does not take that seam, and the gate's surface is unchanged. It is carried
   by SpatialEngine-ffc.

## Alternatives

- **Leave the source scan as the only enforcement.** Rejected: it is precisely
  the thing that can stop working while staying green, which is the defect this
  record answers.
- **Give the architecture guard a reference to the integration suites so it can
  assert the refusal directly.** Rejected: it would bring Testcontainers,
  Npgsql and SqlClient into the lightweight, always-run architecture suite, and
  it asks one suite to read another's types — the coupling ADR-0189 declined
  for the container policies. The refusal facts live where the fixtures do.
- **Assert the refusal by scanning the fixture source for the throw.**
  Rejected: that is the same textual enforcement under a new name, and a
  textual check on a throw is exactly what fails quietly.
- **Share one refusal helper between the suites.** Rejected on ADR-0189
  decision 5's reasoning: a new shared test project to serve a few lines is a
  harder thing to change later than the copies are.
- **Fail the backstop only on zero matches.** Rejected: a partial drift — a
  literal that still matches one stale file — leaves the rest unread and is just
  as broken. The floor is the suite's own size, which the fixture-sharing facts
  already assert from reflection.

## Not decided

- **Whether the *container* fixture should refuse too.** Today the test classes
  hold the database fixture, and the container fixture's connection string is
  its own administrative handle. A fact that read it unguarded would fail on an
  empty string rather than skip, so the case is already loud; the refusal is on
  the property the facts actually reach.
- **Whether `tools/skip_gate.py` should read skip reasons** (ADR-0187 §Not
  decided, ADR-0189 §Not decided). Unchanged, and carried by SpatialEngine-ffc.
- **What the right floor is.** 10 is where the reflection-based fixture-sharing
  facts already sit. A suite that legitimately shrinks below it will make the
  architecture lane red and the number will move.

## Consequences

- Forgetting `Skip.If` is a loud failure that names the guard, caught at the
  first access rather than by an audit. The invariant is enforced where it is
  met.
- The backstop can no longer become green by matching nothing: a renamed fixture
  or a moved suite turns the architecture lane red with a message saying what to
  update.
- The floor is a maintenance cost. A suite that legitimately drops below ten
  container-backed classes is red until the floor is lowered. That is the price
  of failing loudly, and it is paid once per change instead of silently per run.
- The refusal is a behaviour change to a test fixture, not to a contract, host
  or store: no public surface moves, and no third-party type crosses a boundary.
- The suites' visible results are unchanged where they already ran: the refusal
  facts add one plain fact per suite and no container.

## References

- ADR-0187 (amended: the skip invariant is enforced structurally by the
  fixture's refusal, and the source guard is a vacuity-checked backstop),
  ADR-0189 (amended: the same, for the PostGIS fixture), ADR-0010,
  ADR-0028, ADR-0072, ADR-0073, ADR-0139
- `tests/integration/Spatial.PostGIS.Tests/PostgisDatabaseFixture.cs`,
  `PostgisFixtureRefusalTests.cs`
- `tests/integration/Spatial.SqlServer.Tests/SqlServerDatabaseFixture.cs`,
  `SqlServerFixtureRefusalTests.cs`
- `tests/architecture/Spatial.Architecture.Tests/ContainerSkipGuardTests.cs`
- SpatialEngine-x8a, the defect this record answers, reported out of
  SpatialEngine-8af and SpatialEngine-win

## Measurements

Taken 2026-10-07 in the bead's worktree, `docker info` reachable and both
images already pulled.

| Question | Measurement |
| --- | --- |
| `dotnet test tests/integration/Spatial.PostGIS.Tests` | 150 passed / 0 skipped / 0 failed in **4 m 52 s** (ADR-0189 measured 148 passed / 0 skipped before these two facts existed) |
| `dotnet test tests/integration/Spatial.SqlServer.Tests` | 151 passed / 0 skipped / 0 failed in **2 m 23 s** |
| The refusal facts, no Docker | `PostgisFixtureRefusalTests` 1 passed in 40 ms; `SqlServerFixtureRefusalTests` 1 passed in 31 ms |
| `ContainerSkipGuardTests` (backstop + vacuity refusal) | 3 passed / 0 failed in 102 ms, no Docker |
| The vacuity fact before the floor | `Assert.Throws() Failure: No exception was thrown` — the scan returned nothing and the guard stayed green |
