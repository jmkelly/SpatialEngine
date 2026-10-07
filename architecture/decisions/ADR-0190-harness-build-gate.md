---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: Every measurement harness under `eng/` — the `*.csproj` files `SpatialEngine.slnx` deliberately does not name — is **derived and built by `tools/spike_harnesses.py`, and every lane and the CI `verify` job call it**, because being outside the solution is what keeps a harness out of the coverage and metrics gates and is also what kept it out of every build: `tools/verify_scope.py` walks the `ProjectReference` graph from the solution's own project list and the harnesses are `<IsTestProject>false</IsTestProject>`, so `eng/spike-u2x-query-baseline` had stopped compiling entirely by 2026-10-02 and SpatialEngine-58d had to port it by hand before it could re-measure anything. The set is derived rather than listed, so a harness added tomorrow is gated without anybody remembering a list; the check is called directly rather than left to the `tools/**`-only tooling suite, because the change that breaks a harness is a `src` change (ADR-0143, ADR-0146). It costs ~15 s warm for the two of them.
amends: ADR-0118, ADR-0134, ADR-0143
---

# ADR-0190: a harness outside the solution is still compiled by every lane

## Context

`eng/spike-u2x-query-baseline` and `eng/spike-u2x-tile-cache` are committed
measurement harnesses — the numbers the `SpatialEngine-u2x` Tier 1 branch is
justified by live in `eng/spike-u2x-query-baseline/RESULTS.md` and are quoted
in decision records. Each csproj says, in a comment, that it lives under
`eng/` and is **deliberately NOT in `SpatialEngine.slnx`**, so that the build,
coverage and metrics gates stay exactly as they were. That is the right call:
a spike that measured a decision is not code that deserves a coverage floor or
a CRAP score.

The omission had a second consequence nobody wrote down. Nothing in the
repository compiled those projects:

* `tools/verify_scope.py` derives the fast lane's build list by walking the
  `ProjectReference` graph **from the solution's own project list**
  (`load_repository`), and follows references out of it to a fixpoint — but it
  starts at the solution. A project no solution project references is not in
  the graph, so it is in no lane's build, whatever the change set is.
* Both harnesses declare `<IsTestProject>false</IsTestProject>`, so `dotnet test`
  ignores them even when they are in the solution under test.
* Nothing builds `eng/**` by path, and no lane runs `eng/spike-*.sh`.

So the harness drifted as the engine moved under it. By 2026-10-02
`eng/spike-u2x-query-baseline` had stopped compiling entirely:
`EsriFilterClause` had become `EsriWhere`, `IFeatureStore.QueryAsync` had
taken a `FeatureQuery` and answered a `FeatureQueryPage`, and path C threw
where it had used to answer empty. `SpatialEngine-58d` had to port it before
it could re-measure anything, and the port took a while.

The cost is not the failed measurement. It is that **a committed harness's whole
value is that it still runs when the next person needs the number**, and the
thing that was supposed to hold that value — the note at the top of
`RESULTS.md` saying when it was last ported — had already been written and had
not worked. It is maintained by the person whose harness just rotted, and it
records no *next* change to `Spatial.Core`. Every other always-on rule in this
repository is a check, not a note, because a note is sediment
(writing-for-agents, *Pruning*) and this one was no exception.

## Decision

**Derive the ungated harnesses and build them in every lane, through
`tools/spike_harnesses.py`.**

### 1. The set is derived, not listed

The rule is *"no project under `eng/` is left out of every build"*, implemented
as: every `*.csproj` under `eng/`, minus the project paths
`SpatialEngine.slnx` names. A harness added tomorrow is gated without anybody
remembering this record or a list in it; a harness moved into the solution
stops being built here rather than being built twice.

`obj/`, `bin/` and the rest of `IGNORED_DIR_NAMES` are excluded: a `csproj`
under one of them is a regenerated copy, not a harness. An **unreadable or
missing solution reads as naming nothing**, not everything, because the
failure this rule exists for is a harness silently excluded from every build —
an answer it cannot read must widen the set rather than narrow it.

### 2. It is a check the lanes call directly, beside the others

`spike_harness_step` in `eng/verify.sh` runs on the format lane, the full lane
and the fast lane — the merge gate — and the CI `verify` job carries the same
command as its own step. It is not a `tools/test_*.py`: that suite runs only
when the change set touches `tools/**` (`run_python_tooling`,
`tools/verify_scope.py`), and **the change that breaks a harness is a `src`
change**. This is ADR-0143's and ADR-0146's hole in a new place, and it is
closed the way they closed it. `tools/test_spike_harnesses.py` keeps the unit
tests over the discovery, the build contract and the wiring, and the build
contract is tested against a `dotnet` shim rather than a real build: a unit
test that really builds two harnesses costs minutes in a suite that otherwise
takes seconds.

### 3. It is a repository check, so it is not scoped

The step runs before the scoped build, outside `whitespace_step`'s changed-file
narrowing and with no unscoped fallback, for the reason
`doc_surface_step` (ADR-0148) and `beads_gate_step` (ADR-0152) are not scoped:
it is about what the repository ships, not about the change, and narrowing it
to the change set would be the defect. A documentation-only merge still builds
the harnesses.

### 4. It fails loudly rather than failing open

`--list` prints the set and needs no SDK, so the wiring is checkable on a box
without one; the build needs `dotnet`, and a `dotnet` that is not on `PATH` is
a **failure**, not a skip. A check that could not run must not read as a clean
gate — that is the whole class of false green ADR-0143 and ADR-0146 are about.
A harness whose build fails is named in the output alongside the compiler's
own diagnostics.

## Alternatives

- **Fold the harnesses into a solution folder the fast gate builds.** Rejected:
  it puts two `OutputType=Exe` spike projects into the solution the coverage,
  metrics and CRAP gates read, which is the exclusion the csproj comment says
  exists on purpose. It also couples the merge gate's cost to the harness graph
  rather than gating exactly what the solution leaves out.
- **A dated note at the top of `RESULTS.md`.** Rejected: it is what exists
  today and it did not work (see Context). It is also written by the person
  whose harness rotted.
- **A `tools/test_*.py` over a static list.** Rejected on both counts the ADR
  already settled: it runs only when `tools/**` changed, which is never the
  change that breaks a harness, and a hand-maintained list is a fourth thing to
  forget.

## Not decided

Whether the harnesses should be *run* by a lane — they measure, they take
minutes and some need a database, so no. What is decided is that they
**compile**, which is the half that was silently absent and is cheap.

Whether `dotnet format` should cover `eng/**`. ADR-0134 took formatting off the
merge path, so the harness sources are not format-checked today either; the
spikes are measurement scripts and the cost is the same either way. A format
lane scoped to them is a separate change and not a hole.

## Consequences

- The fast lane builds two more projects: measured **7.8 s and 4.8 s** warm on
  this host once their dependencies are built (~30 s cold, and cold is the
  price CI pays once per run), against a gate measured in minutes. It runs
  before the scoped build, so the dependencies it shares are already warm.
- A harness that stops compiling is now a **red merge gate on the `src` change
  that broke it**, not a port somebody discovers weeks later — which is the
  difference the bead this record came from is about.
- A new harness under `eng/` is gated by construction. Nobody has to remember
  to register it, because there is no registry.
- Nothing joins `SpatialEngine.slnx`: coverage, metrics and CRAP read the
  solution, and a spike is not what those are for.
- The failure mode moves from silent to loud. A lane on a box without the .NET
  SDK now fails this step — which is the intended reading of a check that could
  not run, and the first cost this record accepts.
- The `--plan` output and the CI `verify` job both name the step, so the gate's
  scope is visible without opening either file.

## References

- `eng/spike-u2x-query-baseline/` and `eng/spike-u2x-tile-cache/`, the two
  harnesses, and `eng/spike-u2x-*.sh`, the scripts that run them.
- `eng/verify.sh` — `spike_harness_step`, added to all three lanes;
  `.github/workflows/ci.yml` — the `Spike harnesses` step in `verify`.
- `tools/spike_harnesses.py` (the check) and `tools/test_spike_harnesses.py`
  (the discovery, build-contract and wiring tests).
- `tools/verify_scope.py` — `load_repository`, the walk that starts at the
  solution; ADR-0118 (the lanes) as amended by ADR-0134 (the fast lane is the
  merge gate); ADR-0143 and ADR-0146 (a rule that runs only when `tools/**`
  changed is not a gate); ADR-0148 and ADR-0152 (a repository check the lanes
  call directly).
- SpatialEngine-b60 (this bead) and SpatialEngine-58d (the port, and the
  re-measurement it was blocking).

## Measurements

| Question | Answer |
| --- | --- |
| What does the check build? | `eng/spike-u2x-query-baseline/Spatial.Spike.QueryBaseline.csproj` and `eng/spike-u2x-tile-cache/Spatial.Spike.TileCache.csproj` |
| What does it cost? | 7.8 s and 4.8 s warm (`dotnet build` per harness, 2026-10-02); ~15 s for the check end to end |
| Why did nothing catch the drift? | `tools/verify_scope.py` walks the reference graph from the solution's own project list, and both harnesses are `IsTestProject=false` |
| When did the harness last compile? | not after the contract moved: `EsriFilterClause` → `EsriWhere`, `QueryAsync(FeatureQuery)` → `FeatureQueryPage`; SpatialEngine-58d ported it by hand on 2026-10-02 |
| What did the previous answer cost? | a dated note at the top of `RESULTS.md`, which did not survive one contract change |
