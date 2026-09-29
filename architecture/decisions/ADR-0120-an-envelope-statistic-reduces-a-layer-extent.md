---
status: accepted
date: 2026-09-29
deciders: maintainer + agent
---

# ADR-0120: An envelope statistic, so a layer's extent is reduced at the store

## Context

ADR-0112 moved five whole-layer reads in the GeoServices adapter onto faces the
tree already had, and one of them it could only half move. The MapServer layer
extent became a *projected* store read: the plan names the geometry column, so
the columns that are not geometry no longer cross — but every **row** still
did, because the union was taken in managed code over what came back. ADR-0112
filed the rest as a follow-up and named the reason exactly: "the aggregate
vocabulary has no envelope statistic".

That was true, and the reason it was true is a shape the vocabulary had never
had to carry. Every `AggregateStatistic` reduces a field's *values* to a number
— a count, a sum, a percentile — and every reduced value travels as one
`AttributeValue`. A rectangle is not a number, and the layer's extent is the one
answer the GeoServices surfaces ask for repeatedly: a map root's
`fullExtent`, a layer's `extent`, and a `returnExtentOnly` feature query, which
ADR-0112 also left on the match path for the same reason.

The three ways out were all contract changes of some kind:

- **a new store face** — `IFeatureExtentStore.ExtentAsync`, returning an
  `Envelope`. Smallest diff, and it leaves the reduction vocabulary saying that
  an extent is a *different kind of thing* from a statistic, so every future
  caller has two questions to ask a store instead of one.
- **four double values per statistic** — an envelope flattened into
  `xmin,ymin,xmax,ymax` columns. Breaks the one-value-per-statistic contract
  that `AggregatePage`'s `ValueNames`/`Values` and every consumer rely on, and
  makes a grouped envelope four groups' worth of nothing.
- **a reduced value kind** — `AttributeKind.Envelope`, which no field may
  declare and only a reduction produces.

## Decision

**1. `AggregateStatistic.Envelope` is the smallest rectangle over a geometry
field's non-null values.** It is the reference reduction's
`AggregateStatistic.Envelope` case and nothing else, so the answer is defined
before any store has one: a group with no non-null geometry is a **null**, the
same "no values to reduce" answer every other statistic gives — never the empty
rectangle and never a rectangle at the origin. `ResultKind` reports
`AttributeKind.Envelope`, so a store declares the kind of what it returns the
way it does for every other statistic.

**2. `AttributeKind.Envelope` is a reduced kind, not a column kind.** A field
cannot declare it (`FieldDefinition` refuses it, by name, as it refuses
`Null`), and the feature-batch codec rejects the kind byte as a malformed
field. Nothing in a feature row can be one, so no store row mapper, no codec and
no writer had to grow a case for it; the value is boxed in the
`AttributeValue`'s reference slot, which is where the kinds that are already
reference types live. Equality is structural over the four bounds, and the
value ordering compares the lower-left corner and then the upper-right, so the
comparer stays total over a kind it accepts.

**3. The envelope reduces geometries and takes nothing else.**
`FeatureQueryValidation` is where that is decided, alongside the rules it
already holds: an envelope of a numeric field and a sum of a geometry field are
both `invalid.arguments`, by name, at the boundary that received them.

**4. PostGIS pushes it as `ST_AsEWKB(ST_Extent(geom)::geometry)`.**
`ST_Extent` answers a `box2d`, which this provider has no reader for; casting
it to the rectangle's own geometry means the *one* EWKB reader it already has
reads the pushed value, and the row mapper reduces that geometry to its
envelope. Over a group whose geometries are all null it answers `NULL`, which
is the reference's answer, with no translation. The in-memory, demo, ArcGIS
REST and SQL Server faces need no dialect of their own: they already reduce
through `FeatureReduction`, so the statistic is in them the moment the
reference has it.

**5. The layer extent is that reduction, and a table layer's is not a read at
all.** `MapServerResources.ExtentAsync` asks `FeatureReductionFallback` for the
envelope of the geometry column and reads a null as the empty extent. A layer
with no geometry field has no rectangle to reduce, so its extent is the union
over nothing — `Envelope.Empty` — and the table is not scanned to discover
that none of its features has one.

**6. `returnExtentOnly` is the same reduction over the match, with two
refusals.** The plan carries the restriction alone (an extent spans the match,
so a page or a projection over it would answer a different question) and the
response is the one the match path wrote, byte for byte. Two requests keep the
match path: a layer with no geometry field, for the reason above; and a
reprojecting `outSR`, because this response has always been the union of the
**reprojected** geometries, and reprojecting a rectangle and reprojecting the
geometries inside it are different operations.

## Consequences

- **A layer's extent is one aggregate row.** The live PostGIS test asserts it
  as a call count — `ScanAsync` zero, `AggregateAsync` one — beside the value
  comparison against the reference over the same table, which is the pair that
  says "the store answered it" and "it is the right answer".
- **The conformance suite carries the statistic** wherever the schema has a
  geometry field, grouped and ungrouped, over every plan the suite already
  tries. It is the only statistic whose answer is not a scalar, so a store that
  mapped it to a number would agree with the reference on nothing else in the
  row. That is what makes the SQL Server and ArcGIS REST faces' envelopes
  *tested* rather than assumed: their conformance runs are the same suite.
- **The served bytes do not move.** `GeoServicesMapTests` (the map root and
  layer metadata over a live PostGIS-backed host) and the GeoServices host
  suite are unchanged, and the adapter tests pair every pushed answer with the
  match-path answer it replaced.
- **A rectangle cannot be a column.** That is the point of a reduced kind, and
  it is enforced in three places rather than one, because a kind byte a codec
  accepts but a `FieldDefinition` refuses is a malformed-input path that throws
  instead of reporting.
- **The SQL Server face gets the statistic for free, not by pushdown.** It
  reduces through the reference today; this record does not change that, and a
  future T-SQL `ST_Extent` is the same question the reference already answers.

## Alternatives

- **A separate `ExtentAsync` face on the store.** Rejected: it makes an extent
  a different kind of request from every other reduction, so the statistics
  path and the extent path would drift — as they had.
- **Flatten the rectangle into four double values.** Rejected: it breaks
  one-value-per-statistic, which `AggregatePage` and every consumer of it are
  written against, and a grouped envelope would then be four values per group.
- **Report the envelope as a `Geometry` value** (the rectangle as a polygon).
  Rejected: it is a lossy lie about the answer — a caller asking for an extent
  would get a geometry back and have to know that this geometry is a box — and
  it would make the pushed value indistinguishable from a stored one.
- **Keep `returnExtentOnly` on the match path.** Rejected on the merits, and
  the refusals in decision 6 are what make it safe: a reprojecting `outSR`
  genuinely is a different question, and a table layer genuinely has nothing to
  reduce.
- **Give `ST_Extent`'s `box2d` a reader in the provider.** Rejected: a second
  geometry reader for one aggregate expression is a permanent surface, where the
  cast keeps the reduction inside the reader the provider already has and
  trusts.

## References

- ADR-0112 (the projection pushdown this completes, and the follow-up it
  filed), ADR-0115 (the reduction face and the value rules), ADR-0098 §6/§7
  (the reductions and the served query plan), ADR-0074 §1/§4/§6 (validation,
  pushdown, reduction), ADR-0048 (MapServer resources), ADR-0020 (canonical
  binary interchange), Principles 1, 2, 6, 8, 15, 17.
- `src/Spatial.Core/Features/AttributeKind.cs`, `AttributeValue.cs`,
  `FieldDefinition.cs`, `src/Spatial.Core/Features/Query/FeatureAggregates.cs`,
  `src/Spatial.Contracts/FeatureQueryValidation.cs`,
  `src/Spatial.Querying/FeatureReduction.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisPlanQueries.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisRowMapper.cs`,
  `src/Spatial.Adapter.GeoServices/MapServerResources.cs`,
  `src/Spatial.Adapter.GeoServices/StoreQueryPath.cs`,
  `tests/conformance/Spatial.QueryConformance/QueryConformanceSuite.cs`,
  `architecture/distilled/contracts.md` (the store read faces).
