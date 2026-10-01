---
status: accepted
date: 2026-10-02
deciders: maintainer + agent
summary: T-SQL **does** have the geometry aggregate ADR-0133 §3 said it did not — `geometry::EnvelopeAggregate` and `geometry::UnionAggregate([geom]).STEnvelope()`, one expression, `NULL` for a group with no non-null geometry — and the SQL Server store still does not push the envelope, because the rectangle that expression answers is not the rectangle the reference reports. `STEnvelope` grows any box with a zero-width or zero-height side by the server's **1e-8 tolerance**, so every single-member group comes back slightly larger (the shared conformance fixture is degenerate in both axes for every group, and the suite compares values as text); and both aggregates raise a **.NET error 24144** on a group holding a self-intersecting ring, which this store writes, reads and reduces today. The follow-up ADR-0137 filed is closed, and its two open questions answer the same way: `geography` has no instance `STEnvelope` at all, and a mixed-SRID column's union answers `NULL` silently. The reduction is finished by the shared reference, which is now guarded by two tests that fail if the expression is ever pushed (amends 0133, 0137).
amends: ADR-0133, ADR-0137
---

# ADR-0157: T-SQL has a geometry aggregate, and the rectangle it answers is not ours

## Context

ADR-0133 §3 declined to push the envelope onto the SQL Server store because
"a rectangle is four reduced coordinates and a polygon rather than one
aggregate expression", and its `Rejected` section declined the T-SQL spelling a
second time as `MIN`/`MAX` over `STXMin()`/`STYMin()` assembled as text. ADR-0137
pushed the percentile and the boolean extreme, named the envelope as the one
statistic left with an expression available, and filed the rest as a follow-up
rather than taking it, "because it is a second geometry expression and a second
set of SRID facts for one statistic".

That follow-up is this record, and the premise of both refusals was checked
rather than assumed. The premise was half wrong in a way that matters: T-SQL
*has* the aggregate. `geometry::EnvelopeAggregate([geom])` and
`geometry::UnionAggregate([geom]).STEnvelope()` are both one expression over the
column, both answer `NULL` for a group whose geometries are all null — which is
the reference's answer for a reduction of nothing (ADR-0120) — and both read
back through `SqlServerWkb`, the one WKB reader this store already has. The
`STXMin()` spelling ADR-0133 rejected is genuinely gone from the dialect
(SQL Server 2012 replaced it with properties the server's own spatial type
assembly does not expose to T-SQL, error 6506, measured), which is what sent
the follow-up looking for an aggregate in the first place.

So the follow-up's three open questions — the SRID, geography versus geometry,
and the column's layout — had become answerable, and the answer is what this
record is: the expression is measurable, and what it measures is not the
contract's rectangle.

## Decision

**Keep the envelope un-pushed on this store, and say the measured reason rather
than the dialect one.** Two independent facts, either of which is disqualifying,
both taken from a SQL Server 2022 container:

1. **`STEnvelope` answers a tolerance-grown rectangle for a degenerate box.**
   Where a side has zero width or zero height — a group of one point, or any
   group whose members share an x or a y — the server answers a rectangle grown
   by 1e-8 on that axis, where the reference reports the degenerate one
   (`FeatureReduction.BoundingRectangle`, ADR-0120). A group of one member is
   not an edge case: it is the shape the shared conformance fixture has for
   every group, because the fixture's geometries are points on a diagonal. The
   suite compares reduced values as text, so pushing would have turned four of
   the fixture's five groups red by 1e-8 — the very measurement the follow-up
   was filed to gain, and it says the answer is no.
2. **Both geometry aggregates raise on an invalid geometry.** A group holding a
   self-intersecting ring makes `UnionAggregate` and `EnvelopeAggregate` fail
   with a .NET error (24144, "the instance is not valid") — a hard error, not a
   null. Nothing in this store refuses to write such a ring, to read it, or to
   reduce it: the read path takes `STAsBinary()` and the shared reference folds
   the vertices. Pushing would turn a statistics request that answers today
   into a failed one for every layer that holds one, which real data does.

So `SqlServerPlanQueries.Aggregate` answers `null` for a reduction naming an
envelope, the store finishes it with the shared reference over the rows the plan
selected, and ADR-0133's reason for that — the one this record amends — is
corrected rather than repeated: the aggregate exists; the rectangle it answers
is somebody else's.

The follow-up's other two questions are answered here so that they are not asked
again: **`geography` cannot be reduced this way at all**, because
`geography::UnionAggregate(…).STEnvelope()` does not compile on this server
(error 6506 — `SqlGeography` has no instance `STEnvelope`), before the datum
question is even reached; and a **mixed-SRID column answers `NULL`**, silently,
for the union over its whole contents and for a group that mixes two SRIDs —
so a pushdown here would also be a silent null where the reference has a
rectangle, on any hand-authored table whose values do not all carry the same
SRID (ADR-0073: this store reads tables it did not create).

## Alternatives

- **`geometry::UnionAggregate([geom]).STEnvelope().STAsBinary()`, the
  follow-up's candidate.** Rejected on both measurements above. It is the
  shortest statement and the most obviously correct-looking one, and it is
  wrong in the common case rather than the rare one.
- **`geometry::EnvelopeAggregate([geom])`, the aggregate written for exactly
  this.** Measured: the same 1e-8 tolerance on a degenerate box, and the same
  24144 on an invalid one. It is `STEnvelope` by another name.
- **Push the *union* and reduce the rectangle in process, from the union's WKB.**
  This one is exact — it never asks the server for bounds, and the envelope of
  a union is the union of the members' envelopes — and it was the option this
  record preferred while measuring. It is rejected because it inherits the
  24144 unchanged, and because the value crossing the wire is then the union
  rather than a box: for a point layer that is as many bytes as the layer, so
  the "no rows cross the wire" argument buys much less than it does for the
  other statistics.
- **`MakeValid()` each row before aggregating.** Rejected, measured: SQL Server
  says so itself ("MakeValid may cause the points of a geometry instance to
  shift slightly"), and it does — a bowtie's vertex came back as
  `1.0000000000000036` — so the rectangle would be the reference's to within
  nothing, and different in the fifth decimal.
- **`MIN`/`MAX` over `STXMin()`/`STYMin()`, ADR-0133's rejected spelling.**
  Rejected twice over: the methods no longer exist on the dialect this store
  supports (error 6506), and reading four bounds as text is a second,
  text-shaped geometry reader for one statistic — the cost ADR-0137 named.

## Not decided

- **An optimistic pushdown with a fallback seam** — run the pushed statement,
  and when the server raises, re-run the reduction through the reference over
  the same rows. It would make an exact union-based pushdown viable, and it is
  the only way past the 24144 that does not also change the answer. It is not
  taken here because it is a mechanism this store does not have (a caught
  `SqlException` as control flow, and a statement's failure as a normal
  outcome), not a fact about the dialect, and adopting it is its own decision
  with its own cost.
- **What a `geography` layer's reduction should be.** The contract's rectangle
  is planar (ADR-0120), so a geodetic layer's envelope is a datum question
  (reproject to what, and when) rather than an aggregate question. Nothing here
  decides it.

## Consequences

- **The envelope stays the one statistic this store reduces in managed code**,
  and so does every `outStatistics` request that names one — including the
  served extent path (`StoreQueryPath`, `MapServerResources`), which reads the
  layer to answer a layer's extent. That cost is unchanged from ADR-0133; what
  changed is that it is now argued rather than deferred, and the follow-up is
  closed rather than open.
- **The shared conformance suite still measures the fallback on this store**,
  because its reductions carry the envelope. ADR-0137 filed that as the thing
  pushing the envelope would fix; this record says the fix is not available in
  this dialect, so the suite's silence about the pushed path here is the
  correct state and not a gap to be closed. The pushed percentile stays measured
  by `SqlServerStatisticsPushdownTests` for the same reason.
- **Two tests guard the decline**, and both fail if a branch pushes the
  candidate: `An_envelope_is_the_reference_rectangle_and_a_one_point_group_is_not_the_server_s_tolerance`
  (the 1e-8, on the store's own answer) and `A_group_holding_an_invalid_geometry_still_reduces`
  (the 24144, on a table the store describes and reads today). The unit test
  `An_envelope_is_reduced_here` still pins the statement, and its reason is now
  the measured one.
- **ADR-0133's `Rejected` bullet on the envelope is stale** — it declines a
  spelling the dialect no longer has — and its §3 reason is wrong, in the way
  this record measured. Both are amended here. Nothing in ADR-0133's or
  ADR-0137's *decisions* changes: the envelope was not pushed before and is not
  pushed now, and a store's reduction is a cost, never a different answer.
- **The cost of a hand-authored table with mixed SRIDs is unchanged**: the
  store reads such a column and stamps the dataset's SRID on every value. The
  pushdown would have added a second, quieter version of that compromise
  (a `NULL` envelope), which is one more reason it was not worth taking.

## References

- Principles 6 (contracts outlive implementations), 10 (stores are providers),
  15 (pushdown optional, semantics-preserving), 17 (small kernel).
- ADR-0073 (the SQL Server provider, and the tables it did not create),
  ADR-0098 §3 (the reduction semantics), ADR-0115 (the statistics reduction and
  the reference's null rules), ADR-0120 (the envelope as a reduction),
  ADR-0133 §3 and its `Rejected` section (the refusals this record corrects),
  ADR-0137 (the follow-up this record closes, and the two facts that remain).
- `src/Spatial.Stores.SqlServer/Core/SqlServerPlanQueries.cs` (the declined
  statistic, and the two comments that carry the measured reasons),
  `src/Spatial.Stores.SqlServer/Geometry/SqlServerWkb.cs` (the one reader the
  candidate would have read through),
  `src/Spatial.Querying/FeatureReduction.cs` (the reference's rectangle),
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerReductionPushdownTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerStatisticsPushdownTests.cs`
  (the two guards), `tests/conformance/Spatial.QueryConformance/QueryFixture.cs`
  (the degenerate fixture), bead SpatialEngine-6zc.

## Measurements

All against the project's own image, `mcr.microsoft.com/mssql/server:2022-latest`
(RTM-CU27, 16.0.4295.3), via `sqlcmd` in the container, 2026-10-02.

| Statement (over a table of `geometry` at SRID 4326) | Answer |
| --- | --- |
| `geometry::UnionAggregate` over `POINT(1 1)`, `POINT(5 5)`, `NULL` | `MULTIPOINT ((5 5), (1 1))` |
| the same over the group holding only a `NULL` | `NULL` |
| `.STEnvelope()` of `NULL` | `NULL` |
| `UnionAggregate` over `POINT(1 1)` alone, then `.STEnvelope()` | `POLYGON ((0.99999998 0.99999998, 1.00000002 …, 1.00000002 1.00000002, …))` |
| the same over `POINT(1 1)` + `POINT(1 5)` (zero width) | `POLYGON ((0.99999998 1, 1.00000002 1, 1.00000002 5, 0.99999998 5, …))` |
| the same over `POINT(1 1)` + `POINT(5 5)` (both sides positive) | `POLYGON ((1 1, 5 1, 5 5, 1 5, 1 1))` — exact |
| `geometry::EnvelopeAggregate` over the same three cases | identical to `UnionAggregate(…).STEnvelope()`, tolerance and all |
| the candidate statement over the **conformance fixture** (points `(x, x)`, grouped by category) | `A` → `POLYGON ((1.99999996 …, 2.00000004 …))`, `a` → `(0.99999998 … 1.00000002)`, `_c` → `(8.99999982 … 9.00000018)`, the null-keyed group → `(2.99999994 … 3.00000006)`; the reference's four are `2 2 2 2`, `1 1 1 1`, `9 9 9 9`, `3 3 3 3` |
| `UnionAggregate` over a column holding 4326 and 3857 values, and over one *group* mixing them | `NULL`, `NULL` — no error |
| `UnionAggregate` / `EnvelopeAggregate` over `POLYGON((0 0, 2 2, 2 0, 0 2, 0 0))` (a bowtie) | .NET error 24144, "the instance is not valid" |
| the store's own description sample and read path over a table whose *first* row is a valid point and whose second is that bowtie | `4326, Point` and both rows' `STAsBinary()` — the dataset is describable and readable, and reduces |
| `geography::UnionAggregate([geom]).STEnvelope()` | error 6506, "Could not find method 'STEnvelope' for type SqlGeography" |
| `geometry::STXMin()`, `geometry::STXMin(g)`, `g.XMin`, `geometry::Envelope(g)` | error 6506 each — the ADR-0133 spelling and its 2012 replacements are all unavailable to T-SQL here |
| `bowtie.MakeValid().STAsText()` | `MULTIPOLYGON (((2 7.1054273576010019E-15, 2 2, 1.0000000000000036 1.0000000000000036, …)))` — the points shift |
