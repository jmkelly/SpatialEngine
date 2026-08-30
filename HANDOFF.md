# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). Phase 2
> (features and schemas) is complete; Phase 3 (capability runtime) is next.

## Repository state

- **Branch:** `main`.
- **Recent commits:** Phase 0 (`10ab8a8`), Phase 1 (`01fd3fb`, `1841471`),
  Phase 2 (this handoff). **A quality-loop implementor session may still have
  uncommitted Phase-1 mutation-hardening edits in the working tree**
  (Envelope.cs, GeometryCodec.cs, *EdgeTests/*MutationTests files) — see
  "Concurrent quality loop" below. Stage carefully; do not sweep those into a
  feature commit.
- **Solution:** `SpatialEngine.slnx`.
- **Verification:** `./eng/verify.sh` (format check + build + full test run).

## Completed

### Phase 0 — guardrails (commit `10ab8a8`)

Solution layout, central versions, architecture tests (core has no
dependencies, runtime references no plugins, host references only platform
projects, package allowlist, no inline versions, no Tauri in web clients,
exact solution membership). ADRs ADR-0001…0021 committed.

### Phase 1 — core geometry (commits `01fd3fb`, `1841471`)

Coordinate layouts, CRS identity, envelopes, packed/array coordinate
sequences, immutable simple-feature hierarchy, traversal, builders,
`GeometryCodec` (canonical binary v1, spec in `architecture/geometry-model.md`),
`GeometryComparer`. Zero dependencies.

### Phase 2 — features and schemas (this handoff)

All in `src/Spatial.Core/Features/`, namespace `Spatial.Core.Features`,
zero dependencies:

- `AttributeKind` — explicit byte values (wire-stable).
- `AttributeValue` — **boxing-free** tagged union (bool/long/double/Guid/
  DateTimeOffset inline; string/geometry are references; default is `Null`).
  Structural equality: NaN-equal doubles, ordinal strings,
  `GeometryComparer`, date-times by UTC ticks **and** offset (stricter than
  BCL instant-only equality, so equal ⇒ identical bytes). Kind-checking
  getters throw actionable errors. CA1720 is suppressed repo-wide for the
  enum member names (documented in `.editorconfig`).
- `FieldDefinition` — name/kind (never Null)/nullable/description; equality
  includes description; `IsDecodableFrom` (name+kind equal, reader-nullable ≥
  writer-nullable).
- `FeatureSchema` — ordered, ordinal-unique names, `IndexOf`,
  `IsDecodableFrom`/`TryIsDecodableFrom` (**append-only prefix rule** with
  actionable reasons), content equality incl. descriptions.
- `FeatureId` — non-empty string.
- `Feature` — id + schema + validated parallel attribute list (count, kind,
  nullability at construction; defensive copies); indexers by index and name.
- `FeatureBatch` — features must carry the exact batch schema (content
  equality); defensive copy; content equality.
- `FeatureBatchCodec` — canonical batch binary v1, spec in
  `architecture/feature-model.md`. Magic `SFBAT`, strict UTF-8, little-endian;
  length/remaining guards before allocation; deterministic (equal ⇒ identical
  bytes); geometry attributes embed `GeometryCodec` bytes; date-times as
  int64 UTC ticks + int16 whole-minute offset with numerical range checks so
  hostile buffers can never throw `ArgumentOutOfRangeException`;
  `FeatureBatchFormatException` + `TryDecode` errors carry byte offsets.
  **Projection overloads** `Decode(data, targetSchema)` validate prefix
  compatibility — the schema-evolution test vehicle.

Tests (`tests/unit/Spatial.Core.Tests/`): `AttributeValueTests`,
`FeatureSchemaTests`, `FeatureTests`, `FeatureBatchCodecTests` (round trips
for every kind, determinism, projection matrix, ~25 malformed-input cases),
`FeatureBatchCodecPropertyTests` (seeded 1/7/42/1337/20260214/987654321,
random schemas/features, round trip + projection to every prefix). Note:
random strings are built from whole pieces, never lone surrogate halves
(strict UTF-8 replaces them and round trips break).

Docs: `architecture/feature-model.md` (batch v1 spec), plan §16/§21/§25
status ticks, `README.md` status, `src/Spatial.Core/AGENTS.md` owned list.

## Concurrent quality loop — READ BEFORE COMMITTING

A quality-loop run is **active on this repo** (`quality-loop.py` + a
`quality-implementor` pi session, started ~19:48/20:02). It edits Phase 1
files directly in the working tree (no commits). At handoff time it had
uncommitted changes to `Envelope.cs`, `GeometryCodec.cs`,
`CoordinateSequenceTests.cs`, `EnvelopeTests.cs`, `GeometryCodecTests.cs`,
`GeometryConstructionTests.cs` plus new `CodecEdgeTests.cs`,
`EnvelopeMutationTests.cs`, `GeometryEdgeTests.cs` — mostly removing
"unreachable defensive guards" (finite bounds make empty-envelope checks
redundant) and pinning error strings to kill surviving mutants. Its last
iteration (unguarded `Envelope.Union`) was *broken* for `Empty.Union(Empty)`
(mid-edit). Actions:

1. Check `implementor-summary.txt` in
   `~/.pi/sessions/quality-implementor/SpatialEngine-*/` and the queue files
   before trusting the tree.
2. Commit only your own files (`git add` explicitly); leave its edits
   unstaged for its pass or review them first.
3. Do not run Stryker or the full quality loop while it is mid-pass —
   concurrent Stryker runs thrash `StrykerOutput/` and the audits will flap.
   Fast gates (crap/coverage/metrics/warnings) are safe.
4. The loop's gates are: CRAP < 10 per method (already passing), coverage ≥
   70% branch (passing at 85–90%), metrics 0 findings, zero warnings, Stryker
   ≥ 60% (was 26.24% — the implementor is driving this).

## Hard-won gotchas (read before touching geometry/feature code)

- **XYM layout packs M at offset +2** (not +3); XYZM at +3. Property test
  caught the original bug — keep it.
- **DateTimeOffset**: BCL accepts only whole-minute offsets; store UTC ticks
  + minutes; reconstruct via
  `new DateTimeOffset(new DateTime(utcTicks + offsetTicks, DateTimeKind.Unspecified), TimeSpan.FromMinutes(offsetMinutes))`
  (the `(long, TimeSpan)` ctor treats ticks as *local* — wrong).
  Valid offsets are ±840 minutes; valid `UtcTicks` stay within
  `DateTime.MaxValue.Ticks`.
- Default interface members (`ICoordinateSequence.GetCoordinate`) are only
  callable through the interface — tests must cast.
- `dotnet-crap` gate: complexity ≤ 9 per authored method; flag `_` arms into
  helper dispatch throws (the FeatureBatchCodec does this). Also
  `long-parameter-list` flags ≥ 7 params — the codec passes a small
  `AttributeSite` struct to avoid it.
- Unreachable defensive branches tank Cobertura line-rate — keep error paths
  reachable (the implementor is removing such guards).
- Keep tests deterministic: seeded property tests, no randomness in
  fixtures; strings in property tests must be whole pieces (see above).
- `dotnet format` requires a final newline in every file.
- `ThrowIfNullOrWhiteSpace(null)` throws `ArgumentNullException`, not
  `ArgumentException` — tests must expect the right one.

## Next up — Phase 3: Capability Runtime

From the plan (§16, Epic D, §6.1 "Runtime model"): lands in
`src/Spatial.Runtime` (currently an empty project) and
`src/Spatial.PluginSdk` (empty). Scope per plan:

1. Capability descriptors (versioned ids like `spatial.geometry.buffer@1`,
   purpose, input/output schemas, error variants, permissions, side effects,
   streaming/cancellation traits, provenance fields) — see
   `architecture/capability-model.md`.
2. Registry with deterministic resolution (explicit provider → resource-local
   → configured preferred → first healthy by stable id).
3. Invocation routing with structured errors, deadlines, cancellation and
   permissions.
4. In-memory component host + example capability (the plan's test vehicle).
5. Tests: resolution order, descriptor validation, error paths, cancellation,
   no plugin references from the runtime (architecture tests already guard).

Contracts with public behaviour must land in `src/Spatial.PluginSdk` (the
SDK ships contracts; the runtime routes). Phase 4 (resources, streams, jobs)
builds on it. Keep every method cc ≤ 9 and run the fast quality gates; run
the full loop only when no implementor pass is in flight.

## Commands

```bash
./eng/verify.sh                 # format + build + all tests (required before done)
python3 ~/.pi/agent/skills/quality-loop/scripts/quality-loop.py  # full gate loop (~15 min)
# single gates, fast feedback:
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/audit.py           # CRAP
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/coverage-audit.py  # branch coverage
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/metrics-audit.py   # MI/cyclomatic/params
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/warnings-audit.py  # zero-warning build
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/stryker-audit.py   # mutation (~11 min)
```