---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
summary: The dataset description carries the coordinate layout its store declares, and the layer metadata advertises `hasZ`/`hasM` exactly to that extent.
---

# ADR-0084: Advertise `hasZ`/`hasM` from the coordinate layout the store declares

## Context

The Feature Server layer resource (§9.1) carries `hasZ` and `hasM`. They are
not capability flags like the `supportsX` family ADR-0081 governs — they are a
**description of the data**, and ArcGIS clients branch on them: the ArcGIS REST
JS and the JS API read `layer.hasZ` and only then send Z in query geometry and
edit payloads, and a client that is told a 2D layer has Z will send Z the
engine has no way to round-trip. The research pass had listed both under the
10.x additions a real client expects (`research/arcgis/conformance-sources.md`
§1.1; `architecture/references/geoservices-compatibility.md` §7 names
`hasZ/hasM` alongside `returnCountOnly` as an expected 10.x key).

Until now the layer metadata said nothing about Z/M at all, and — this is the
part that made a print-a-flag fix dishonest — **the engine had nowhere to learn
it from**. `DatasetDescription` carried the geometry column, its SRID and its
type name (`POINT`, `POLYGON`, …), and `CoordinateLayout` existed in
`Spatial.Core` as a per-coordinate-sequence structural property. Nothing tied
the two together at the dataset level, so any value on the wire would have been
a guess.

ADR-0081's rule is the rule this record applies, and it is stricter here than
for capability flags: a `supportsX: false` costs a client a code path it can
avoid, but a `hasZ: true` on a 2D dataset makes it *send data the engine cannot
store back*. Under-advertise, never over-advertise.

## Decision

**The dataset description carries the coordinate layout its store declares, and
the layer metadata advertises `hasZ`/`hasM` exactly to that extent — true when
the store proves the ordinate, absent otherwise.**

### 1. `DatasetDescription.GeometryLayout`

A new optional trailing positional parameter, typed `CoordinateLayout` (a
`Spatial.Core` type, so the Contracts wall is intact) and defaulting to
`CoordinateLayout.Xy`. The default is the honest answer, not an omission: a
store that cannot prove an ordinate reports the dataset as two-dimensional, and
every consumer — the GeoServices layer, the TypeScript SDK — can trust a `true`
here. It appears on `GET /api/datasets/{id}` as `geometryLayout`
(`"xy" | "xyz" | "xym" | "xyzm"`, default `"xy"`) and on the generated
TypeScript SDK type as an optional `CoordinateLayout`.

### 2. PostGIS learns it from the declared column type

`PostgisSchemaDiscovery` now takes each column's formatted type modifier from a
new catalogue read (`pg_catalog.format_type(atttypid, atttypmod)`) and the pure
`PostgisCoordinateLayout` parser reads the Z/M suffix off the geometry type
name: `geometry(PointZ,4326)` → `Xyz`, `geometry(PointM,4326)` → `Xym`,
`geometry(PointZM,4326)` → `Xyzm`, `geometry(Point,4326)` → `Xy`.

A typmod is a **type-system proof**: a column declared `geometry(PointZ,4326)`
cannot hold a coordinate without a Z, so the flag is true of every row without
reading a row. It is a catalogue read, so it costs no scan and cannot disagree
with the schema.

A column declared plain `geometry` constrains nothing, so it proves nothing
either and is reported `Xy`. This is a deliberate **under-report**: such a
column may well hold Z values the engine will faithfully serve, but the schema
is not evidence, and the flag is a claim. The same rule covers an unparseable
modifier — the parse is a proof, never a guess.

### 3. Every other store reports `Xy`

SQL Server's `geometry`/`geography` types have no Z or M storage at all, so it
lands on the default and always will. The ArcGIS REST provider started here
too, and **ADR-0091 amends this section**: it proves the layout from the remote
layer's own `hasZ`/`hasM` declaration — the same class of schema evidence as a
declared column type — and asks the remote for the ordinates it advertises, so
the flag and the read path cannot drift.

### 4. The layer metadata

`EsriLayer` gains `HasZ` and `HasM` as `bool?`. They are `true` only when the
declared layout carries the ordinate and `null` — which
`EsriJson.Options` already drops on the wire — otherwise. A two-dimensional
layer therefore **omits** both keys rather than reporting `false`, and a table
(no geometry field) omits them regardless of what its description says.

## Consequences

- A client preflighting a layer now learns, before it asks, whether the dataset
  carries elevations or measures. Previously it learned nothing and guessed.
- A dataset whose geometry column is declared without a typmod still advertises
  nothing, even when its values carry a Z. This is the cost of the honesty
  rule, and it is the right side of the trade: the alternative is a flag the
  schema cannot back.
- `hasZ: true` is a claim the store must keep backing. If a store ever begins
  reporting a layout its read or write path cannot round-trip, the flag has to
  go with it — the same coupling ADR-0081 established for `supportsQuantization`.
- The `/api/datasets/{id}` response gains an optional key. The OpenAPI snapshot
  and the generated TypeScript type are regenerated; existing clients are
  unaffected because the field is optional with a default.
- Nothing crosses a new wall. `CoordinateLayout` already lived in
  `Spatial.Core`; the Esri wire shapes stay inside `Spatial.Adapter.GeoServices`
  (ADR-0005, ADR-0035); `Spatial.Core` and the render pipeline are untouched.

## References

- ADR-0005 (implementation types stay in their implementation)
- ADR-0028 (PostGIS store and schema discovery)
- ADR-0035 (GeoServices boundary adapter)
- ADR-0081 (advertised query capability flags — the honesty rule applied)
- ADR-0091 (amends §3: the ArcGIS REST store proves its ordinates from the
  layer's own declaration)
- `architecture/references/geoservices-compatibility.md` §4, §7
- `research/arcgis/conformance-sources.md` §1.1–1.2
- `research/compat/feature-service.md` §1–2
