---
status: accepted
date: 2026-09-27
deciders: maintainer + agent
summary: A relationship is a declaration on the map's layer, traversed by the ordinary query path; relate and unrelate are edits behind the edit gate.
---

# ADR-0077: Relationships are declared on the map, traversed with the query engine

## Context

ADR-0037 made feature editing a capability and ADR-0065/0066 made
attachments another, so a per-feature sub-resource (`attachments`) has a model
behind it. A relationship had none: `queryRelatedRecords` was mounted nowhere
and `geoservices-compatibility.md` §7.1 listed it, with attachments, as
"no engine model behind them; absent, not emulated". Attachments have since
landed, so relationships are the remaining item on that list.

What the engine had was the two halves of a relationship without a way to say
they were related: two datasets in a store, and identity columns on each
(`DatasetDescription.IdColumns`). What it lacked was the declaration itself
and a read path across it. The constraint that decides where the declaration
goes is principle 10 — stores are providers, the domain model is the
publication — so a store cannot hold it: the same two datasets may be
published with different relationships, and a store that declared one would
be asserting something about a map it has never seen.

The constraints are the AGENTS.md hard walls: `Spatial.Core` stays structural
(inspection, traversal, encoding, envelopes — a relationship declaration is
none of those); public contracts carry only core types; no new packages in
`Spatial.Contracts`; long-running work is a cancellable `Task`; failures are
structured `SpatialException` codes.

## Decision

**A relationship is a declaration on the map's layer, validated
structurally when the map is stored and against the live schemas where a
declaration happens; the traversal is the ordinary query path pointed at
another layer; relate and unrelate are edits behind the existing edit gate.**

### 1. Where the declaration lives

`MapLayer` carries `Relationships`: each is a `LayerRelationship` naming a
`RelatedLayerId` of the same map, a `PrimaryKeyColumn` on this layer, a
`RelatedKeyColumn` on the related layer, a `LayerRelationshipCardinality`
(one-to-one, one-to-many, many-to-many) and, for many-to-many, a
`LayerRelationshipJoin` naming a join dataset and its two key columns. All of
it is core-typed vocabulary in `Spatial.Contracts`; the Esri
`esriRelationshipType*` names stay in the adapter.

The relationship names a **layer id**, not a dataset. That is what makes
`relatedLayerId` resolvable in the metadata a client reads, and it pins the
traversal to a layer the service actually publishes.

The join dataset is not a special table: it is an ordinary dataset (usually
without geometry, so it is served as a `Table`) carrying the two keys. The
engine holds no join semantics; the declaration only says which columns carry
them, and the stores stay plain feature datasets.

### 2. Validation happens twice, where each fact is available

`MapValidator` (Spatial.Maps, pure) checks the shape: the relationship name is
identifier-shaped and unique per layer, the columns are identifiers (so a
declaration can never smuggle SQL structure into the grammar the traversal
builds from it), the target is another published feature layer, the
many-to-many cardinality is paired with a join dataset and no other
cardinality is, and the cardinality itself is a known member.

`MapRelationshipSchemas` checks what only a store can answer: the named
datasets exist, the named columns exist in their schemas, the two sides carry
the **same key kind** (otherwise the equality could never match), and a join
dataset's two columns match the kinds of the layers they link. It runs where
declaration happens — the neutral `PUT /api/maps/{name}` route, next to the
existing layer-servability check — so a relationship over a column that does
not exist is `invalid.arguments` at declaration time rather than a service
that answers no rows forever. Configuration-declared maps carry the same
declarations through `Spatial:Maps:Declared`.

### 3. The traversal is the query engine

`queryRelatedRecords` renders the origin record's key as a `column = value`
term in the **existing closed where-grammar**, parses it with the engine's own
parser, ANDs the related layer's requested `where` onto it, and runs the
shared match, ordering and projection path. So the related layer's `where`,
`outFields`, `geometry`/`spatialRel`, `time` and `outSR` all apply, and the
client's text never becomes SQL structure. A many-to-many relationship reads
its join rows first and disjoins the keys they name; an origin record the
join does not reach answers the constant false term, never every record.

The response is the spec §9.1.5.6 shape: the related layer's `fields` and one
`relationships` entry per origin record that has related records, each with
the key it relates through as `relatedId` and the projected rows as `fields`.
Origin records with no related records are omitted rather than served empty.
The result shapes that do not apply to a traversal (`returnIdsOnly`,
`returnCountOnly`, `returnExtentOnly`, `returnDistinctValues`, `outStatistics`,
`uniqueIds`, paging) are rejected by name, each naming the operation that
does serve it.

Reads are public, like `query`: they read the same records. A layer that
declares nothing advertises nothing, and the `relationships` key is omitted
rather than served empty.

### 4. Relate and unrelate are edits

`relate` (`objectIds` + `relateIds` + `relationshipId`) sets the related
record's key column; `unrelate` clears it. A many-to-many relationship adds
or deletes a join row instead, because that is where the relationship lives.
Both are mounted in `FeatureEditEndpoints` and travel **the same admin gate as
`addFeatures`/`updateFeatures`/`deleteFeatures`** (ADR-0065 §3, ADR-0071) — a
relationship change moves a key on a stored record, so a new mechanism would
have been a second, weaker gate. They need the `IFeatureEditStore` face of the
store holding the record being changed, and report one result per
origin/related pair the `applyEdits` way, so a partial failure never hides
behind one error. An unrelate of a pair that is not related, and a key column
that cannot be nulled, are per-pair typed failures.

### 5. Failures and cancellation

Every step observes its `CancellationToken`, including the per-record loops,
and the fakes in the tests ignore their token so the traversal's own checks
are what is proved. Unknown layers and origin records are `not.found`;
malformed ids, an unresolvable `relationshipId`, a key kind with no literal
and the rejected result shapes are `invalid.arguments`; a missing editing face
is `invalid.arguments`. A declaration-time failure is a structured
`SpatialException` through the neutral API, not an Esri envelope.

## Consequences

- Contract: `LayerRelationship`, `LayerRelationshipCardinality`,
  `LayerRelationshipJoin` and `MapLayer.Relationships` in
  `Spatial.Contracts` — core types only, no new package, no `Spatial.Core`
  change. The .NET SDK needs no new surface: it forwards the `Map` contract
  type verbatim (`SpatialMapClient.PutMapAsync`), so a declaration made
  through the SDK carries its relationships.
- Spatial.Maps: structural validation in `MapValidator`, live-schema
  validation in `MapRelationshipSchemas`, and declared-configuration parsing
  in `MapRegistry`/`MapsOptions`.
- Spatial.Host: `EnsureLayersAreServableAsync` also validates relationships
  at the declaration boundary.
- Spatial.Adapter.GeoServices: the served `relationships` metadata
  (`EsriRelationshipModel`), the traversal (`FeatureRelationshipEngine`,
  `RelatedQuery`, `FeatureRelationshipTargets`, `FeatureRelationshipKeys`),
  the writes (`FeatureRelationshipWrites`), and the
  `queryRelatedRecords`/`relate`/`unrelate` routes.
- The composition is store-agnostic: a relationship over the memory store
  works over PostGIS or SQL Server with no provider change, because the
  traversal uses the ordinary feature-read and editing faces.
- `relationshipId` is the declared **name** (or its one-based position); the
  engine assigns no numeric relationship ids, and `relationships[].id` is
  that name. Clients keying on numeric relationship ids need the name.
- Composite keys and self-referencing relationships stay out of scope: the
  declaration names one column per side, so a composite key is not
  expressible and is rejected as a missing column.
- `esriRelationshipTypeComposition`, `isComposite` and relationship
  cardinality constraints are not modelled; a one-to-one is a declaration, not
  an enforced uniqueness constraint.

## Alternatives

- **Holding the declaration in the store (a relationship table):** rejected —
  it makes a provider assert something about a publication it has never seen
  (principle 10), and the same datasets could not be published two ways.
- **Deriving relationships from naming conventions (`parent_id` implies a
  parent):** rejected — it would make every layer with a foreign-key-shaped
  column advertise a relationship, which no client can distinguish from a
  deliberate one, and it would couple the wire model to column names.
- **A dedicated relationship query verb in the contracts (a
  `IRelationshipReader`):** rejected — the traversal is the query engine
  pointed at another layer, and a new verb would duplicate filtering,
  projection and paging for no capability the existing faces lack.
- **Numeric relationship ids:** rejected — the engine has no id space for
  them; the declared name is already unique per layer and is what a client
  reads back from the metadata.
- **Gating relate/unrelate on a new mechanism (per-relationship tokens, or
  ungated like `addFeatures`):** rejected — the edit gate already exists
  (ADR-0037/ADR-0065/ADR-0071) and a relationship change is an edit, so it
  travels that gate unchanged.
