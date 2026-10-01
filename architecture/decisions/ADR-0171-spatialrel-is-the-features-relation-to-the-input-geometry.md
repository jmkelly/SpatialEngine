---
status: accepted
date: 2026-10-03
deciders: maintainer + agent
summary: **Serve `esriSpatialRelContains` and `esriSpatialRelWithin` in the protocol's frame — the relation of the feature to the input geometry — so the served `Contains` is the feature contained in the query geometry (`T*F**F***`) and the served `Within` is the feature containing it (`T*****FF*`), the transpose of what the table served before.** ArcGIS Server is the authority and it serves it this way round: a point query geometry over a county layer answers `Within` → 1 and `Contains` → 0. The other four verbs do not move, because the OGC masks for `Touches`, `Overlaps`, `Crosses` and `Intersects` are closed under transposition.
amends: ADR-0106
related: ADR-0169, ADR-0166, ADR-0156, ADR-0036
---

# ADR-0171: `spatialRel` is the feature's relation to the input geometry

## Context

ADR-0106 settled the served `spatialRel` table — the OGC Simple Features
DE-9IM masks, asked over `IGeometryRelations.Relate` — and deliberately left
one question open: **which geometry is the left operand of the matrix**. It
pinned the reading in the feature-left frame without claiming the frame was
the right one, and named this bead.

The served path has been feature-left since SpatialEngine-u2x.2. That frame is
not a detail, because two of the six verbs are antisymmetric and the other four
are not:

- `Touches`, `Overlaps`, `Crosses` and `Intersects` name a relation OGC
  defines as symmetric, and their masks are closed under transposition
  (`T*T***T**` transposes to itself, `T**T*****` transposes to `T*T******`,
  the four intersect patterns transpose into themselves, the touches union is
  a union of masks that transpose into the union). So **the bead's premise that
  all five verbs are inverted is wrong**: transposing the operands cannot move
  a single verdict of the four.
- `Contains` and `Within` are the transpose of one another, and which name
  carries which mask is exactly the open question. Feature-left, the served
  answers were the OGC reading of the *feature*: a point inside a county
  answered `esriSpatialRelContains`, which is not what the parameter means.

Two independent sources say the protocol wants the other frame, and both were
checked while writing this record:

1. **The reference.** `spatialRel` is documented as "the spatial relationship
   to be applied to the **input geometry**" — the relation named is the input
   geometry's, so `esriSpatialRelWithin` is "the feature contains the input
   geometry". The `spatialRel` parameter's own description carries the frame;
   the per-value list it points at does not restate it, which is why the
   question survived as long as it did.
2. **A live FeatureServer.** `sampleserver6.arcgisonline.com/arcgis/rest/
   services/USA/MapServer/3/query`, layer *Counties*:

   | request | `esriSpatialRelContains` | `esriSpatialRelWithin` |
   | --- | --- | --- |
   | point at (-71.5, 41.6), inside one county | 0 | **1** |
   | box covering six counties | **6** | 0 |

   The point is inside the feature and the feature contains the input geometry,
   so `Within` answers 1. The box is the other way round and `Contains`
   answers 6. The provider's frame is the opposite of the served one on both
   verbs, and it is the same on both operand sizes — so it is the frame, not an
   artefact of one geometry.

The direction also reaches the Geometry Service: `relation` over
`geometries1`/`geometries2` resolves every served verb out of the same
`SpatialRelationPredicates` entries with its first geometry as the feature
(ADR-0106 §4), so a decision about the frame is a decision about both
surfaces or about neither.

## Decision

**Read the matrix with the feature on the left and the two antisymmetric verbs
in the protocol's frame: the served name states the feature's relation to the
input geometry, so `esriSpatialRelContains` is `T*F**F***` and
`esriSpatialRelWithin` is `T*****FF*`.**

1. **`Contains` is the feature *contained in* the query geometry.** Served mask
   `T*F**F***` over `(feature, query)`, with the feature's envelope inside the
   query geometry's as its pre-filter.
2. **`Within` is the feature *containing* the query geometry.** Served mask
   `T*****FF*`, with the query geometry's envelope inside the feature's. It
   remains the transpose of `Contains` (ADR-0106), read in the other frame.
3. **The other four verbs do not move, and no row of them is rewritten.** Their
   masks are transpose-closed, so the frame cannot change them; the fixtures,
   the hand-computed matrices and the verdict table move only in the
   `Contains` and `Within` columns.
4. **One table, both surfaces.** The Geometry Service's `relation` operation
   answers the same way for the same ordered pair, by the same table rather
   than by a second copy (ADR-0106 §4, SpatialEngine-dih).
5. **Both operand orders are pinned.** Every served verb is asked over a
   nesting — a feature strictly larger than the query geometry, and the same
   feature strictly smaller — so the direction is a test and not a reading of
   whichever table happens to be in the tree.

## Alternatives

- **Keep the feature-left frame and record why.** Rejected: a live FeatureServer
  answers the opposite on both verbs and both operand sizes, and the
  parameter's own documentation says the relation is applied to the input
  geometry. There is no evidence for the served frame beyond the accident that
  it was written first, and the two readings are indistinguishable on the four
  symmetric verbs, which is exactly how a wrong frame stays invisible for
  years.
- **Swap the operands of every verb.** Rejected: it is a no-op for four of the
  six and a bug factory for `Crosses`, whose two mixed-dimension masks are
  selected *by* the operand order (`T**T*****` with the area on the left,
  `T*T******` with it on the right). Swapping the operands there would select
  the wrong mask and answer the ADR-0169 divergence in a new direction. The
  transposition is done at the mask, by name, for the two verbs that need it.
- **Serve both frames behind a parameter.** Rejected: there is one `spatialRel`
  parameter with a closed grammar, and an agreed-but-inverted second reading
  is a second set of answers to one question with nothing to choose between
  them — the state ADR-0106's "keep the two implementations" alternative
  already rejected.
- **Leave the Geometry Service on the feature-first frame** because the
  GeometryServer's own `relate` could not be observed to disagree.
  Rejected: the two surfaces sharing one table is the property that makes the
  direction auditable (ADR-0106 §4), and a surface that keeps the old frame
  would answer the same verb differently from the same server. It is named
  below as the one place the evidence is inference rather than observation.

## Not decided

- **Whether `Contains` against a point input geometry is the strict mask or
  the `covers` mask** (SpatialEngine-aqy), and **`Crosses` for a point strictly
  inside an area** (SpatialEngine-msc). Neither is settled by this record and
  neither is the frame: a live server answers the *direction* question above
  cleanly and says nothing about either edge. Both still want an observation
  against a real FeatureServer.
- **What the Geometry Server's own `relate` does with a named relation.** The
  online `utility.arcgisonline.com` GeometryServer returned an empty
  `relatedGeometries` body for every `esriSpatialRel*` name tried
  (`relate` with `geometries1`/`geometries2`, `Contains` and `Within`, both
  operand orders), so the operation could not be observed either way and its
  direction is settled here by the shared table rather than by measurement.

## Consequences

- **Every `Contains` and `Within` answer on the served surface inverts.** A
  client that found features containing a query geometry by sending
  `esriSpatialRelContains` now gets the features it contains instead, and one
  that found the contained features by sending `Within` now gets the
  containers. Both are what the parameter means, and both were wrong before,
  but the change is observable and is the point of the record.
- **A client sending `esriSpatialRelContains` for a point-in-polygon query
  starts working.** That is the commonest use of the parameter in a JS or
  Python client, and it returned nothing before.
- **The DE-9IM columns are unchanged and stay feature-left.** The matrix a
  fixture records is a property of the pair, not of the verb; only the two
  verdict columns move, and `SpatialRelationMatrix` says so.
- **A store pushdown that answered a relation with its own predicates has to
  read the frame the same way** — `esriSpatialRelContains` is `ST_Within` and
  `esriSpatialRelWithin` is `ST_Contains` on the pair `(feature, query)`. The
  adapter-side-only decision (ADR-0167) means no shipped store does this today;
  `SpatialRelationPushdownTests` pins the mapping so a store face added later
  inherits it.
- **Cost:** the ArcGIS REST store forwards the `spatialRel` name to a remote
  FeatureServer, so a client asking the facade and the remote the same question
  now gets the same answer — but a client that compared the facade with a
  *previous release* of the facade, or with the OGC table read straight, sees
  the two verbs swapped. That is a breaking change to a served surface and is
  recorded as one rather than absorbed.

## References

- ADR-0106 (the served table and the question this record answers), ADR-0036
  (the relation verbs), ADR-0166 (the engine's `Relate` is the DE-9IM matrix),
  ADR-0169 (the `Crosses` frame dependence, unchanged by this record),
  ADR-0156 (the two independent readers of the table), ADR-0167 (the plan
  carries the envelope and the relation stays adapter-side)
- `src/Spatial.Adapter.GeoServices/SpatialRelationPredicates.cs` — the two
  masks and their pre-filters
- `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelDirectionTests.cs` —
  both operand orders for every served verb
- `tests/unit/Spatial.Adapter.GeoServices.Tests/SpatialRelationMatrix.cs` — the
  two verdict columns that moved
- `tests/integration/Spatial.Host.Tests/GeoServicesSpatialRelTests.cs` — the
  point-against-envelope case over HTTP
- `architecture/references/geoservices-compatibility.md` §2 and §7.1
- SpatialEngine-2ve (this decision), SpatialEngine-u2x.2 (the feature-first
  reading that stood until now), SpatialEngine-onj (ADR-0106),
  SpatialEngine-aqy and SpatialEngine-msc (the edges still open)

## Measurements

Live ArcGIS Server, 2026-10-03, `curl` against
`sampleserver6.arcgisonline.com/arcgis/rest/services/USA/MapServer/3/query`
(layer *Counties*, polygons), `returnCountOnly=true`, `f=json`, WGS 84:

| input geometry | `esriSpatialRelContains` | `esriSpatialRelWithin` |
| --- | --- | --- |
| `{"x":-71.5,"y":41.6}`, point inside one county | `{"count":0}` | `{"count":1}` |
| ring (-71.9,41.2)-(-71.2,42.1), six counties | `{"count":6}` | `{"count":0}` |

Served behaviour before this record, over the same two cases measured through
the adapter: the point-against-envelope case answered `Contains` 1 /
`Within` 0 (`GeoServicesSpatialRelTests` as it stood), which is the first row
of the table with its two columns exchanged.

`dotnet test --filter FullyQualifiedName~SpatialRelDirectionTests` against the
tree before the fix: 7 cases, 2 failed — `Contains` and `Within` in both
operand orders — and the four symmetric verbs passed. After: 7 passed. The two
failures are the whole of the divergence the direction can produce, which is
the measurement the mask-closedness argument predicts.