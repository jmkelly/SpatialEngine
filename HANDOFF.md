# Handoff — Spatial Engine

> Written for the next agent taking over. Read `AGENTS.md`, then
> `architecture/implementation-plan.md` (the source of truth). Phase 1 is
> complete; Phase 2 (features and schemas) is next.

## Repository state

- **Branch:** `main`, clean working tree.
- **Recent commits:** `eadfb14` (audit-artifact ignore + stryker config) ·
  `1841471` (quality-loop complexity reductions) · `01fd3fb` (Phase 1: core
  geometry) · `10ab8a8` (Phase 0: guardrails).
- **Solution:** `SpatialEngine.slnx` (XML solution format — note the quality
  loop tooling was patched to support it, see below).
- **Verification:** `./eng/verify.sh` (format check + build + full test run).
  Currently: 8 architecture + 144 core unit + 3 host integration tests, all
  passing.

## Completed

### Phase 0 — guardrails (commit `10ab8a8`)

Solution layout, `Directory.Packages.props` (central versions), architecture
tests (`tests/architecture`) enforcing: core has no dependencies, runtime
references no plugins, host references only platform projects, package
allowlist, no inline versions, no Tauri in web clients, exact solution
membership. All ADRs ADR-0001…0021 committed.

### Phase 1 — core geometry (commits `01fd3fb`, `1841471`)

All in `src/Spatial.Core/Geometry/`, namespace `Spatial.Core.Geometry`,
zero dependencies:

- `Coordinate` (record struct, `double? Z/M`), `Ordinate`, `CoordinateLayout`
  (`Xy/Xyz/Xym/Xyzm`) with `OrdinateCount/HasZ/HasM/Infer`.
- `CoordinateReference` — authority+code identity, `Epsg(int)`; validation;
  **value types are the structural default** (record structs with explicit
  constructors — C# 12 primary constructors cannot have validation bodies).
- `Envelope` — finite-bounds struct, `Empty` (infinities), NaN-skipping
  computed creators; explicit construction rejects NaN/infinity.
- `ICoordinateSequence` + `PackedCoordinateSequence` (one `double[]` per
  sequence) + `ArrayCoordinateSequence` + `CoordinateSequenceComparer`
  (ordinate-wise equality across backings, NaN-equal semantics).
- Simple-feature geometry hierarchy: `IGeometry`, `Point`, `LineString`,
  `Polygon`, `MultiPoint`, `MultiLineString`, `MultiPolygon`,
  `GeometryCollection` (+ `GeometryCollectionBase<TChild>`), all immutable
  with defensive copies.
- `GeometryFactory` (builders; composites carry **at most one distinct
  non-null CRS** — conflicts throw), `GeometryTraversal` (`Parts`,
  `DepthFirst`, `Coordinates`), `GeometryComparer` (exact structural
  equality/hash for `IGeometry`).
- `GeometryCodec` — canonical binary v1, documented in
  `architecture/geometry-model.md`. Header `SGEOM` + version byte;
  self-describing nodes (layout, type, CRS per node); **Point body starts
  with an explicit `hasCoordinate` byte** (empty points must be unambiguous
  inside collections); nesting ≤ 256; count-vs-remaining guards; strict
  UTF-8; `TryDecode` errors carry byte offsets; deterministic (`Encode` of
  equal geometries is byte-identical).

### Quality loop (skill-level patches under `~/.pi/agent/skills/quality-loop`)

The loop now runs green for this repo. Patches made to the skill tooling
(all backward-compatible):

- `scripts/quality-loop.py`, `scripts/dotnet/audit.py`,
  `scripts/dotnet/warnings-audit.py` — accept `*.slnx` solutions.
- `scripts/dotnet/audit.py` — runs coverage per test project and merges
  cobertura files (`coverage_common.py`, method-level line-rate maxima);
  rematch layer that fixes crap4dotnet's signature matcher for nullable
  (`Point?` vs `Point`) and `in`-parameter signatures by normalising against
  the cobertura data; multi-test-project namespace exclusion.
- `scripts/dotnet/metrics-audit.py` — gates on the JSON report, not the
  tool's (buggy, always-1) exit code.
- `scripts/dotnet/stryker-audit.py` — skips reference-free test projects and
  prefers projects carrying their own `stryker-config.json`.
- `crap4dotnet` 0.1.1 accepts a directory, not `.slnx` — audit passes the
  repo root in that case.

Repo policy: `tests/unit/Spatial.Core.Tests/stryker-config.json` pins the
mutation target to Spatial.Core (break 60). Audit artifacts
(`crap-queue.md`, `*-report.json`, `stryker-queue.md`, …) are gitignored and
regenerated per run.

**Last gate status:** quality PASS (0/379 CRAP ≥ 10, after complexity
reductions — every authored method is now cc ≤ 9, enforced by the gate),
coverage PASS (85–90% authored branch ≥ 70), metrics PASS (0 findings),
warnings PASS, **Stryker RED — 26.24% < 60% break** (full run, 979
mutants; the initial 89.59% figure was a narrowly-scoped run and is wrong).
The quality loop drives implementor passes against `stryker-queue.md`;
survivors are mostly missing behavioral assertions (layout-merge changes,
boundary comparisons, null/empty guards, mutator arithmetic) — add real
tests, do not lower the break threshold.

## Hard-won gotchas (read before touching geometry code)

- **XYM layout packs M at offset +2** (not +3); XYZM at +3. The sequence
  `GetOrdinate` and `FromCoordinates` both encode this. A property test
  caught the original bug — keep it.
- Default interface members (`ICoordinateSequence.GetCoordinate`) are only
  callable through the interface — tests must cast.
- `dotnet-crap` gate effectively requires **complexity ≤ 9 for every
  authored method** (CRAP = cc²(1−cov) + cc; at full coverage CRAP = cc).
  Keep new methods under cc 10; split switches with `_` arms into helper
  dispatch methods when needed. Also `long-parameter-list` flags ≥ 7 params.
- Unreachable defensive branches tank Cobertura line-rate — prefer reachable
  error paths (e.g. the point-body truncation is reported from the read
  path, not a pre-check).
- Keep tests deterministic: seeded property tests use fixed seeds; no
  randomness in fixtures.

## Next up — Phase 2: Features and Schemas

From the plan (§16, Epic C, §6.1): implement in `src/Spatial.Core/Features/`
(namespace `Spatial.Core.Features`), no dependencies:

1. `AttributeKind` (enum) and `AttributeValue` — a boxing-free tagged union:
   `Null | Boolean | Int64 | Double | String | Geometry | DateTimeOffset |
   Guid`; private per-kind constructors (watch the 7-param rule), public
   `From*` factories, kind-checking getters that throw actionable errors,
   structural equality (geometry values via `GeometryComparer`, NaN-equal
   doubles, ordinal strings).
2. `FieldDefinition` — name, kind (never Null), nullable flag, description;
   `IsDecodableFrom` (name + kind equal, reader-nullable ≥ writer-nullable);
   `FeatureSchema` — ordered fields, duplicate-name rejection, `IndexOf`,
   `IsDecodableFrom` = **prefix compatibility** (reader fields must be a
   decodable prefix of writer fields — the append-only column evolution
   rule); equality includes description.
3. `FeatureId` (non-empty string), `Feature` (id + schema + parallel
   attribute list; count/kind/nullability validated at construction;
   defensive copies), `FeatureBatch` (features must carry the exact batch
   schema).
4. `FeatureBatchCodec` — versioned batch format v1 (magic `SFBAT`, schema
   header, per-feature id + null-marker + schema-driven typed payloads;
   geometry attributes are length-prefixed `GeometryCodec` blobs; strict
   UTF-8; count/bounds guards; `FeatureBatchFormatException`), plus a
   `Decode(data, targetSchema)` projection overload that validates prefix
   compatibility — this is the schema-compatibility test vehicle.
5. Tests: `tests/unit/Spatial.Core.Tests/FeaturesTests.cs` family — value
   semantics, schema compat matrix (exact/prefix/reorder/kind-change/
   nullability rules), round trips with every attribute kind, malformed
   input, determinism, projection.
6. Docs: new `architecture/feature-model.md` (batch v1 spec), ticks for
   Epic C in the plan, README status, `src/Spatial.Core/AGENTS.md` already
   lists the owned types.

Then Phase 3 (capability runtime) and Phase 4 (resources, streams, jobs) —
see plan §16; both land in `src/Spatial.Runtime` (currently empty) and
`src/Spatial.PluginSdk`.

## Commands

```bash
./eng/verify.sh                 # format + build + all tests (required before done)
python3 ~/.pi/agent/skills/quality-loop/scripts/quality-loop.py  # full gate loop (~15 min, runs Stryker)
# single gates, fast feedback:
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/audit.py           # CRAP (~1 min)
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/coverage-audit.py  # branch coverage
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/metrics-audit.py   # MI/cyclomatic/params
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/warnings-audit.py  # zero-warning build
python3 ~/.pi/agent/skills/quality-loop/scripts/dotnet/stryker-audit.py   # mutation (~11 min)
```

Run the quality loop at the end of every phase, not just before handoff.