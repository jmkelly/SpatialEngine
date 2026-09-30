---
status: accepted
date: 2026-09-28
deciders: maintainer + agent
summary: The ArcGIS REST store proves `hasZ`/`hasM` from the remote layer resource and asks the remote for exactly the ordinates the description declares.
amends: ADR-0084
---

# ADR-0091: The ArcGIS REST store proves its ordinates from the layer's own declaration

## Context

ADR-0084 made the dataset description carry the coordinate layout its store
declares, so `hasZ`/`hasM` on the Feature Server layer resource advertise what
the data really carries, and it named the proof it accepts: a **type-system
proof** — a PostGIS column declared `geometry(PointZ,4326)` cannot hold a
coordinate without a Z, so the flag is true of every row without reading one.

It then listed every other store as landing on the `Xy` default, and called
the ArcGIS REST provider out by name as a follow-up. That follow-up was not
cosmetic. The provider is a **proxy**: it serves somebody else's layer
metadata, and it had no path to a 3D dataset at all. `ArcGisRestMapper.Describe`
built a `DatasetDescription` from the remote layer JSON and dropped the
`hasZ`/`hasM` booleans, so a FeatureServer layer that really does carry
elevations was described as `Xy`, and a layer served through the GeoServices
metadata advertised neither key. A client preflighting it concluded the data is
two-dimensional — and, per ADR-0084, that is the safe direction to be wrong in
only for a *local* store guessing about its own schema.

There is a second, sharper reason not to leave it. A Feature Server does not
return the extra ordinates unless the query asks: `returnZ=true`/`returnM=true`
are opt-in (§9.1.4), and the query path sent neither. So advertising `hasZ`
without also asking for Z would have been the exact failure ADR-0084 warns
about — a claim the read path cannot back, in the other direction: the layer
says Z, the bytes come back 2D.

## Decision

**The ArcGIS REST store reads `hasZ`/`hasM` off the remote layer resource, and
its query path asks the remote for exactly the ordinates the description
declares.**

### 1. The layer resource is the proof

`ArcGisRestMapper` maps the layer metadata's `hasZ`/`hasM` booleans through
`CoordinateLayoutExtensions.FromOrdinates` into
`DatasetDescription.GeometryLayout`. The upstream service's own declaration
about its own geometry is the same class of evidence as a declared column type:
it is the schema, not a sample, so it is true of every row the remote serves.

Only the JSON `true` counts. A flag that is absent, `false`, or any other
value kind proves nothing and lands on the default `Xy` — the same rule ADR-0084
applies to an unparseable typmod, and for the same reason: **the parse is a
proof, never a guess.** A malformed flag is not an error either; a remote that
declares nothing is an ordinary 2D layer.

### 2. The query carries the declared ordinates

`returnZ=true` and `returnM=true` are added to the remote query exactly when
the described layout has the ordinate, and a two-dimensional layer asks for
neither — the remote keeps its own default. This is the coupling ADR-0084
already stated as a consequence ("`hasZ: true` is a claim the store must keep
backing"), applied to the store that now makes the claim.

Nothing else moves. The mapper is pure (ADR-0035) and the change is two
booleans read and a layout constructed; the PostGIS DDL path and
`Spatial.Contracts/IDataStores.cs` are untouched, and the contract already had
the field ADR-0084 added.

## Consequences

- A proxied 3D layer is described as 3D, and the GeoServices metadata for it
  advertises `hasZ` — a client that preflights now learns the elevation is
  there, and the query that follows actually returns it.
- The store's advertised flag and its read path are one decision, so they
  cannot drift: a layer with `hasZ` is a layer whose query sends `returnZ`.
- A remote that lies about its own layout is believed, as a remote's schema is
  trusted everywhere else here. The mitigation is the same one PostGIS has —
  the declaration, not a sample, is the evidence, and the value is a
  description rather than an engine-side inference.
- The ADR-0084 "every other store reports `Xy`" list now names one store:
  SQL Server, whose spatial types have no Z or M storage at all.

## References

- ADR-0035 (GeoServices REST boundary adapter — the pure mapper)
- ADR-0081 (advertised capability flags — the honesty rule)
- ADR-0084 (advertise `hasZ`/`hasM` from the declared coordinate layout)
- ADR-0085 (query `returnZ`/`returnM` ordinate output)
- `architecture/references/geoservices-compatibility.md` §4, §7
- `research/compat/feature-service.md` §1
