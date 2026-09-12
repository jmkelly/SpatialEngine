---
status: accepted
date: 2026-09-12
deciders: maintainer + agent
---

# ADR-0040: Metrics gate thresholds are evidence-based, and LCOM4 gates through guarded diagnoses

## Context

The quality loop's metrics gate is `.dependably` at the repo root, read by
`codemetrics` (Dependably.CodeMetrics) through
`~/.pi/agent/skills/quality-loop/scripts/dotnet/metrics-audit.py`. Its
thresholds had been copied verbatim from the tool's README example
(cyclomatic ≤ 25, cognitive ≤ 30, nesting ≤ 5, LCOM4 ≤ 4, in-repo coupling
≤ 20, MI ≥ 20) with `failOn: high`. They were never derived for this
codebase, and measurement showed two independent problems.

### 1. The raw LCOM4/coupling rules bypass the tool's statelessness guards

`codemetrics` ships an interpretation layer (`Diagnostics`) that only
diagnoses LCOM4 and coupling on types with **instance state**
(`FieldCount >= 1`). For stateless types LCOM4 is ≈ method count by
construction (no shared instance state, so unrelated methods never union)
and coupling is high by construction; the tool documents both as
meaningless for such classes. The per-metric `rules` gate
(`RepositoryAnalyzer.CountTypeGateBreaches`) applies **no such guard** — it
is a raw `metric > threshold` count.

Measured on this tree, the raw `lcom4 ≤ 4` / `coupling ≤ 20` rules produced
19 of 26 "high" findings on stateless types (`FeatureService`,
`GeometryService`, `GeoServicesEndpoints`, `PostgisEwkb`, … are all
`static`), none of which is a cohesion or coupling defect. The tool's own
guard-aware findings for the same tree were 1 high god-class
(`SpatialClient`), 1 moderate god-class (`PostgisStore`), 4 low hubs and 6
low long-parameter-list — 12 findings, not 37.

### 2. The complexity thresholds were ~2× the published norms

The scalar thresholds were significantly looser than both the tool's own
severity bands and the published guidance the metrics come from.

| Metric | `codemetrics` severity bands | Published guidance | Old gate |
| --- | --- | --- | --- |
| Cyclomatic | ≥21 high, ≥11 moderate | McCabe: split above 10; NIST permits up to 15. Sonar C# S1541 default = 10 | ≤ 25 |
| Cognitive | ≥25 high, ≥15 moderate | Sonar C# S3776 default = 15 | ≤ 30 |
| Nesting | ≥6 high, ≥4 moderate | ESLint `max-depth` default = 4 | ≤ 5 |
| MI | <20 high | Microsoft Visual Studio green floor = 20 | ≥ 20 |
| LCOM4 | ≥8 high, ≥4 moderate | Hitz & Montazeri: 1 is cohesive, ≥2 signals a class that can be split | ≤ 4 |
| In-repo coupling | ≥40 high, ≥20 moderate | Sahraoui/Godin/Miceli: CBO > 14 too high; NDepend: type efferent coupling > 50 alarming | ≤ 20 |

`failOn: high` was also misleading: because the raw rule thresholds sat at
the tool's **moderate** band edges and a rule breach is a hard error, the
gate effectively failed at moderate severity while claiming to fail at high.

## Decision

Recalibrate `.dependably` to published/band-consistent values and stop
gating the guard-less raw LCOM4 metric:

| Rule | Old | New | Basis |
| --- | --- | --- | --- |
| cyclomatic | max 25 | **max 15** | NIST relaxed ceiling; the CRAP `< 10` gate already caps covered methods at 9, so 15 is a genuine backstop |
| cognitive | max 30 | **max 15** | Sonar C# S3776 default |
| nesting | max 5 | **max 4** | ESLint `max-depth` default; the tool's moderate band |
| lcom4 | max 4 | **off** | raw metric is meaningless for stateless types; the guard-aware `low-cohesion` / `god-class` diagnoses are the correct gate |
| coupling | max 20 | **max 40** | the tool's high band; a pathological ceiling on in-repo fan-out that still catches `FeatureService` (51) |
| mi | min 20 | **min 20** | unchanged — Microsoft green floor |
| `failOn.severity` | `high` | **`moderate`** | makes the previously-implicit effective severity explicit and lets the guard-aware `low-cohesion` / moderate `god-class` diagnoses gate LCOM4 |

`exclude` is unchanged.

## Sources

- Cyclomatic: T.J. McCabe, *A Complexity Measure* (IEEE TSE, 1976);
  NIST SP 500-235 *Structured Testing* (split above 10, relax to 15);
  Sonar C# [S1541 default = 10](https://github.com/SonarSource/sonar-dotnet/blob/master/analyzers/src/SonarAnalyzer.CSharp/Rules/FunctionComplexity.cs).
- Cognitive: Sonar C# [S3776 default = 15](https://github.com/SonarSource/sonar-dotnet/blob/master/analyzers/src/SonarAnalyzer.Core/Rules/CognitiveComplexityBase.cs).
- Nesting: [ESLint `max-depth` default = 4](https://eslint.org/docs/latest/rules/max-depth).
- MI: [Microsoft code metrics — maintainability index range and meaning](https://learn.microsoft.com/en-us/visualstudio/code-quality/code-metrics-maintainability-index-range-and-meaning).
- LCOM4: Hitz & Montazeri, *Measuring Coupling and Cohesion in Object-Oriented Systems* (1995); [Aivosto Project Metrics — cohesion](https://www.aivosto.com/project/help/pm-oo-cohesion.html).
- Coupling: [Aivosto Project Metrics — Chidamber & Kemerer](https://www.aivosto.com/project/help/pm-oo-ck.html)
  (CBO > 14 too high, after Sahraoui/Godin/Miceli); [NDepend code metrics](https://www.ndepend.com/docs/code-metrics)
  (type efferent coupling > 50 alarming).
- Tool bands and statelessness guards: `Dependably.CodeMetrics`
  [`Diagnostics.cs`](https://github.com/dependably/codemetrics/blob/main/src/CodeMetrics/Diagnostics.cs).

## Consequences

- The gate drops from 26 "high" findings to 3 high + 1 moderate, all
  genuine by the tool's own model: `FeatureService` in-repo coupling 51,
  `FeatureService.EditsAsync` cognitive 17, `SpatialClient` god-class
  (high), `PostgisStore` god-class (moderate).
- LCOM4 is no longer a raw gate; a stateful class is flagged only through
  the tool's `low-cohesion` (LCOM4 ≥ 8, instance state, WMC ≥ 20, < 4
  interfaces) or `god-class` diagnoses. This is deliberate: the raw metric
  cannot express the statelessness guard this codebase needs.
- The gate stayed **red** at ADR time. AGENTS.md's list of facades
  awaiting a cohesive per-area split (`FeatureService`, `SpatialClient`,
  `PostgisStore`) was exactly what the gate reported, instead of noise.
- **Resolution (2026-09-12):** those splits landed and the metrics gate is
  now green (0 high, 0 moderate). `FeatureService` became a thin facade
  over `FeatureQueryEngine` / `FeatureEditEngine` / `FeatureGeometry`;
  `SpatialClient` shed HTTP plumbing into `SpatialClientTransport`;
  `PostgisStore` moved its stateless write leaves to
  `PostgisWriteOperations` and its query predicate to `PostgisPredicate`
  (the isolated static helpers were the LCOM4 components, not the
  instance methods); `EsriFilterClause`'s comparison primitives moved to
  `EsriFilterLogic`. The remaining findings are low-only (hubs and
  long-parameter-list) and do not gate.
- If `codemetrics` later adds the `FieldCount >= 1` guard to the raw
  `lcom4`/`coupling` rules, this ADR should be revisited and the LCOM4 rule
  re-enabled at the tool's high band.
- The skill's bundled `.dependably.default` is a generic fallback for repos
  without a policy file; it is intentionally left at the tool example
  values and is not governed by this ADR.
