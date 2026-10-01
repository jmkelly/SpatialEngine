---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
summary: Discovery reads a dataset's SRID unconditionally and its geometry type only from a value the server calls valid, because `STGeometryType()` raises error 24144 on an invalid value — so the sample answers nothing for the type rather than failing the whole statement. A dataset whose sampled geometry is a self-intersecting ring is described and read today (its type falls back to the default, the answer an empty table already gives) instead of being undescribable, and one such dataset no longer fails the whole catalogue listing (amends ADR-0157, ADR-0151).
amends: ADR-0157, ADR-0151
---

# ADR-0164: Discovery reads the geometry type only from a value the server calls valid

## Context

This store discovers an authored table's geometry facts by sampling one of its
values (ADR-0151 §6: SQL Server keeps the SRID with each value rather than on
the column, so the description's SRID and geometry type come from `TOP 1`
sampling). The sample read both facts in one expression:

```sql
SELECT TOP 1 <geom>.STSrid, <geom>.STGeometryType() FROM t WHERE <geom> IS NOT NULL
```

`STSrid` is a property. `STGeometryType()` is a method, and the server refuses
to run it on a value it considers invalid — .NET error 24144, "this operation
cannot be completed because the instance is not valid" (ADR-0157 measured the
same refusal on both `geometry::` aggregates). Nothing in this store refuses to
write such a value, and the read path returns it: `STAsBinary()` hands back the
invalid ring and the shared reference folds its vertices. So the dataset that
holds one **cannot be described at all**, and every request on it fails at the
catalogue rather than at the operation:

```
SpatialException: The SQL Server store failed: A .NET Framework error occurred
during execution of user-defined routine or aggregate "geometry": … 24144 …
   at Microsoft.SqlServer.Types.SqlGeometry.STGeometryType()
```

which reads like a store bug rather than a data fact, and which moves with the
table's physical row order — the same table with a valid point ahead of the
ring describes and reads fine, which is why ADR-0157's guard test wrote its
valid row first. The catalogue listing is worse than one dataset: every dataset
is sampled in a single unioned statement (`SampleAllGeometry`), so one arm
raising fails **every** dataset's listing.

ADR-0157 recorded the 24144 as the reason the envelope is not pushed, and named
this as a cost it accepted for the reduction path only ("a table whose *sampled*
geometry is the invalid one cannot be described at all"). The question it left
open is the one this record answers: whether discovery should read the SRID
without the type, or map an unstattable sample to a diagnosable failure.

## Decision

**Read the SRID unconditionally and the geometry type only from a value the
server has already called valid.** The sampled type becomes
`CASE WHEN <geom>.STIsValid() = 1 THEN <geom>.STGeometryType() END`, in both
sample statements (`SampleGeometry` and `SampleAllGeometry`), and the discovery
code is unchanged: `SqlServerSchemaDiscovery.FactsFrom` already maps a null
sampled type to `SqlServerQueries.DefaultGeometryType` (`"Geometry"`), the
answer an empty table already gives, while keeping the sampled SRID.

SQL Server's `CASE` evaluates its branches in order and stops at the first that
holds, so `STGeometryType()` is only ever run on a value `STIsValid()` has
accepted. That short-circuit is the whole mechanism, and it is stated here
rather than left implicit: it is a property of the dialect, not of the type
system, which is why the two branches are spelled out in one expression
instead of being rearranged into a filter (a `WHERE … STIsValid() = 1` filter
does **not** protect the method — the plan computes the type in a scalar
operator before the filter runs, and the 24144 comes back anyway).

A dataset whose sampled value is invalid is therefore described, not refused:
its SRID is the one its values carry, its type is the default, and its rows
read as they always did. One such dataset no longer fails the catalogue
listing of all the others.

The cost, which is real and is not hidden: **the description's geometry type is
best-effort from an arbitrary row.** A table that is points with one invalid
value in it is described as `"Geometry"` whenever the sampled row is the invalid
one, and as `"Point"` whenever it is not. What that costs downstream is
already bounded and small — `EsriLayerModel.GeometryType` answers
`esriGeometryNull` for an unmapped name and `MapGenerateRenderer`'s symbol
choice falls through to a marker. It is a worse *report* for that dataset, not
a wrong *answer*: no query is generated from the description's type, and the
rows themselves are untouched.

## Alternatives

- **Read the SRID alone and always default the type.** Rejected: it is strictly
  worse than the `CASE` — it throws away the type for every dataset whose
  sampled row *is* valid, which is every well-formed dataset, to avoid a call
  on the ones that are not.
- **Scan for a valid row's type** — `WHERE … AND STIsValid() = 1`, or an
  `ORDER BY` that prefers valid rows — and default only when the table has no
  valid value at all. Rejected twice: the filter does not work (measured, the
  24144 still comes back, because the type is computed before the filter), and
  the ordering variant would run a validity check over a whole table on a
  describe path, per dataset, on the catalogue listing — for a fact that is a
  label rather than an input to any generated SQL.
- **`MakeValid()` the sampled value and read its type.** Rejected on ADR-0157's
  measurement, unchanged here: the server warns that it may shift points, and
  it does, so the type would be read off a geometry that is not the one stored.
- **Map an unstattable sample to a diagnosable failure** — catch the `SqlException`
  and raise `invalid.arguments` or `not.found` naming the dataset. Rejected: it
  needs the fallback seam ADR-0157 filed as "not decided" (a caught exception
  as control flow, and a statement's failure as a normal outcome), and it buys
  a *refusal* where the store can still answer the SRID, list the dataset and
  read every row in it. A dataset that can be served should be served; the
  diagnosis belongs in the data, and the description carries the type that
  could not be read.
- **Take the geometry type from the WKB header** (`STAsBinary()` works on an
  invalid value, so it never raises). Rejected: it is a second geometry-type
  table written in T-SQL, duplicating the one the dialect already has, to read
  a label.

## Not decided

- **Whether an invalid value should be refused on the way in.** This store
  writes, reads and reduces one today, and ADR-0157 measures that it reduces
  correctly. Whether the write path should validate, warn, or keep accepting
  is a question about authoring rather than about discovery, and nothing here
  decides it.
- **Whether the description should say that its geometry type was not read** —
  a fact a client could be told, rather than a `"Geometry"` that reads as a
  claim. `DatasetDescription` carries no such field today and adding one is a
  contract change with its own record.

## Consequences

- A table holding an invalid geometry is describable, listable and readable,
  with the SRID its values carry and the default geometry type. Every request
  on such a dataset now fails — if at all — at the operation that cannot be
  done, not at the catalogue.
- The description's geometry type is best-effort for a dataset with invalid
  values, and is documented as such in the code that samples it. The sample is
  already arbitrary (ADR-0151 §6 accepts exactly this arbitrariness for the
  same two fields), so nothing new is claimed about it.
- `STIsValid()` costs one validity check per sampled row — one row per dataset,
  on the path that was already reading that row. It is not a scan, and it is
  not pushed into any plan that reads rows.
- ADR-0157's guard test wrote its valid row before the bowtie so the sample
  would land on it; that precaution is no longer load-bearing, and the test
  says so rather than keeping a superstition. ADR-0157's own decisions — the
  envelope stays un-pushed, the invalid value still reduces — are unchanged.
- The unsupported-column and non-spatial-dataset refusals are untouched: a
  dataset still fails to describe when its *schema* cannot be read, and still
  fails as `not.found`. This record is about one sampled value's geometry
  type, not about discovery's refusals.

## References

- ADR-0157 (the 24144 on invalid geometries, the read path that tolerates
  what the aggregates refuse, the guard test this record disarms),
  ADR-0151 (discovery by sampling; the arbitrariness of that sample),
  ADR-0028 (discovery from the catalogue), ADR-0073 (the tables this store
  did not create), ADR-0122 (the PostGIS description cache).
- `src/Spatial.Stores.SqlServer/Data/SqlServerQueries.cs` (`SampleGeometry`,
  `SampleAllGeometry`, and the guarded-type expression they share),
  `src/Spatial.Stores.SqlServer/Data/SqlServerSchemaDiscovery.cs`
  (`FactsFrom`, where a null sampled type already became the default),
  `src/Spatial.Stores.SqlServer/SqlServerCatalogue.cs` (the describe and
  listing paths, both unchanged),
  `src/Spatial.Adapter.GeoServices/EsriLayerModel.cs` and
  `MapGenerateRenderer.cs` (what an unmapped geometry type costs),
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerQueryTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerIntegrationTests.cs`,
  `tests/integration/Spatial.SqlServer.Tests/SqlServerStatisticsPushdownTests.cs`
  (ADR-0157's guard), bead SpatialEngine-8kt.

## Measurements

Via `sqlcmd` in the project's `mcr.microsoft.com/mssql/server:2022-latest`
container, 2026-10-01, over a table holding one bowtie
(`POLYGON ((0 0, 2 2, 2 0, 0 2, 0 0))`, SRID 4326) followed by one valid
point.

| Statement | Answer |
| --- | --- |
| `SELECT TOP 1 g.STSrid, g.STGeometryType() …` | error 6522 wrapping .NET 24144 at `SqlGeometry.STGeometryType()` |
| `SELECT TOP 1 g.STSrid, g.IsValid …` | error 6592 — `IsValid` is not a T-SQL property on this type |
| `SELECT TOP 1 g.STSrid, g.IsValid() …` | error 6506 — not a method either |
| `SELECT TOP 1 g.STSrid, g.STIsValid() …` | `4326, 0` — the validity question *is* answerable |
| `SELECT TOP 1 g.STSrid, g.STIsValid() … WHERE g.STIsValid() = 1` | error 6522 / 24144 — the filter does **not** protect the type |
| the same, filtered in a derived table (`FROM (SELECT TOP 1 g … WHERE STIsValid() = 1) v`) | error 6522 / 24144 — a derived table does not protect it either |
| `SELECT TOP 1 g.STSrid, CASE WHEN g.STIsValid() = 1 THEN g.STGeometryType() END …` | `4326, NULL` — no error, and no error over the all-invalid table |
| `SELECT TOP 1 g.STSrid, g.STAsBinary() …` | `4326, 0x…` — the read path answers over the same row |

End-to-end, through the store, on that container
(`SqlServerIntegrationTests`, before and after the change):

| Case | Before | After |
| --- | --- | --- |
| describe a table whose only value is the bowtie | `SpatialException` 24144 | `4326`, type `Geometry`, and the row reads back as a `Polygon` |
| list the catalogue with that table present | `SpatialException` 24144, for *every* dataset | listed, alongside `dbo.places` and the rest |