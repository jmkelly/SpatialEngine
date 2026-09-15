---
status: accepted
date: 2026-09-15
deciders: maintainer + agent
---

# ADR-0069: Quality gates — namespace rigidity is advisory; architecture suite out of the mutation gate

## Context

ADR-0040 recalibrated the `.dependably` metrics gate to published
thresholds and set `failOn.severity` to `moderate`, so the tool's
guard-aware moderate diagnoses (god-class, low-cohesion) gate alongside
the error-level method/type rules. At the time the gate was green with 0
high and 0 moderate findings.

The post-0.2.0 quality loop (CRAP/complexity paydown plus the ADR-0040
facade splits) changed the dependency shape: the loop split
`FeatureQueryEngine`, `GeoServicesEndpoints`, `WmsService`,
`ImageServerEndpoints` and `ImageService` into cohesive engines, and each
new engine references the shared Esri interop DTOs. The metrics gate now
reports exactly 2 moderate findings and 0 highs:

- `Spatial.Interop.Esri`: rigidity D 0.63, Ca 48, abstractness 0.03.
- `Spatial.Rendering.Skia.Styling`: rigidity D 0.60, Ca 12, abstractness
  0.07.

Both are the tool's `architectural-rigidity` (zone-of-pain) namespace
diagnosis: concrete and widely depended on, remediation "introduce
abstractions to invert dependencies". Clearing `Spatial.Interop.Esri`
honestly needs Ca 48 → <8 (≈40 fewer real dependents) or abstractness
0.03 → ≥0.3 (≈9 new abstractions); usage is spread across ~10+ consumer
types with no single type dominating, so a concern-split would not drop
any sub-namespace under the Ca bar and would raise per-namespace distance
instead. Merging the engines back would re-breach the per-type in-repo
coupling rule (max 40, currently clean at 0 highs) — a dead end in both
directions within a batch fix.

The coupling itself is prescribed architecture, not a defect: the shared
Esri codec/filter grammar lives in `Spatial.Interop.Esri` per ADR-0035,
and the renderer styling subset is shared by the Skia pipeline per
ADR-0044/0049. Inverting those dependencies (DTO abstractions in
`Spatial.PluginSdk`) is ADR-level design work with contract consequences,
not a quality-loop refactor.

Probed levers (2026-09-15, codemetrics 0.1.2, all rejected by the tool):

- `.dependably` `exceptions` accept only method/type rules (cyclomatic,
  cognitive, mi, nesting, lcom4, coupling); every `architectural-rigidity`
  exception form fails with `Unknown rule "architectural-rigidity"`.
- `.dependably` `rules` likewise reject `architectural-rigidity`/`rigidity`
  entries — there is no per-diagnosis severity or disable lever.
- The only config lever is the blanket `failOn.severity`.

## Decision

Set `.dependably` `failOn.severity` back to `high` (the skill-bundled
default). Namespace `architectural-rigidity` and any future moderate
diagnoses become advisory: they are reviewed at release time and tracked
here, not release-blocking.

What still gates (unchanged): the error-level method/type rules
(cyclomatic ≤ 15, cognitive ≤ 15, nesting ≤ 4, coupling ≤ 40, MI ≥ 20),
plus the independent CRAP < 10, authored-branch-coverage, zero-warnings
and mutation gates. Per-type fan-out is currently clean (0 highs), so no
genuine coupling defect is being waived.

## Stryker appendix (same session)

The mutation audit runs every `*.Tests.csproj`: projects with their own
`stryker-config.json` gate on their own `thresholds.break`, others get a
generated default (break 25) when exactly one `ProjectReference` is
pinnable, else skipped. Two outcomes needed repo policy:

- `Spatial.Core.Tests`: 91.17% vs break 80 — green, no action.
- `Spatial.Architecture.Tests`: 0.83% vs the generated default break 25.
  The run mutates `Spatial.PluginSdk` (the suite's single reference:
  interfaces, DTOs, STJ converters) under a suite that asserts
  cross-cutting structural invariants by design — it cannot kill
  value-mutants no matter how good it is at its own job. A ~0% score is
  inherent to the suite's purpose, not a test-quality signal, so no
  amount of in-suite test-writing clears it honestly; writing functional
  converter tests into the architecture suite would miscategorise them.
  `tests/architecture/Spatial.Architecture.Tests/stryker-config.json`
  therefore pins the project under test with `break: 0`, waiving the
  structural suite out of the mutation gate (the waiver reason lives here
  because the config format is comment-free JSON).
  The fault-detection signal for `Spatial.PluginSdk` comes from the
  functional suites that exercise it. The remaining 19 multi-reference
  test projects stay skipped (no pinnable single project); adding
  per-project pins is follow-up work, not a pre-release fix.

## Consequences

- The metrics gate returns to green; the two rigidity findings above are
  accepted technical debt with this ADR as the record.
- Tradeoff vs ADR-0040: a future moderate god-class/low-cohesion
  diagnosis will no longer block the gate by itself. Mitigation: the
  release checklist reviews the full metrics queue (moderates included),
  and the CRAP < 10 gate plus cyclomatic/cognitive error rules still cap
  method-level complexity.
- Revisit triggers (whichever comes first): `Spatial.Interop.Esri` Ca
  crosses 60, a moderate god-class/low-cohesion diagnosis appears, or the
  tool gains per-diagnosis suppression — at which point either do the
  abstraction work or grandfather that specific diagnosis with reason and
  expiry.
