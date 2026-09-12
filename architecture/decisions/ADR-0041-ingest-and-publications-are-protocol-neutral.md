---
status: proposed
date: 2026-09-13
deciders: maintainer + agent
---

# ADR-0041: Ingest and publications are protocol-neutral capabilities

## Context

ADR-0035 made Esri GeoServices REST a boundary adapter owned by
implementation projects and explicitly left service *creation* undefined —
the held v1.0 specification has no admin or publish surface. The engine can
serve a static, config-declared service map (`Spatial:GeoServices:Services`)
but cannot ingest data or publish a service at runtime.

Three concrete gaps block the "upload data → create a feature service" use
case:

1. **A service is a whole store.** Each config entry maps one logical name
   to one keyed store, and every dataset in that store becomes a layer. A
   single uploaded dataset cannot be published under its own stable name.
2. **Layer ids are unstable.** The facade assigns a layer id as the index
   into the alphabetically sorted dataset list (the open question recorded
   as S2 in `geoservices-implementation-plan.md`), so adding a dataset
   renumbers existing layers.
3. **Uploaded datasets have no identity and no atomic load.**
   `IDataCatalogue.CreateAsync` builds a table from a sample batch with no
   primary key, and `CreateAsync` + `IFeatureStore.WriteAsync` are two
   non-atomic calls, so a failed load leaves a half-populated table and a
   layer that cannot be edited (ADR-0037) or looked up (ADR-0038).

The Esri admin API that would create services is **not** in the v1.0
specification; it is Esri's proprietary ArcGIS Server surface. Encoding the
product feature as an Esri admin endpoint would make a vendor protocol the
domain model and duplicate it for every future protocol (Map, Image, OGC
API Features).

The engine already has the right precedent for additive capabilities
(ADR-0036/0037/0038): a provider advertises only the faces it owns, and
callers fall back or reject. Ingest and publication are two such faces;
they are protocol-neutral because they operate on datasets and keyed
stores, not on any wire format.

## Decision

**1. A *publication* is the neutral unit of exposure.**

A publication is a named, ordered projection of datasets from one keyed
store onto a protocol surface. It is core-typed and carries no Esri
concept:

```csharp
public enum PublicationKind { Feature, Map, Image }

public sealed record PublicationLayer(string Dataset, int LayerId, string? Name = null);

public sealed record Publication(
    string Name, PublicationKind Kind, string Store,
    IReadOnlyList<PublicationLayer> Layers,
    string? Description = null, string? Copyright = null);
```

`LayerId` is assigned once, persisted, append-only and never reused or
renumbered; removing a layer leaves a gap. This closes S2. Declared
(config) services are projected to publications at startup so the config
shape is preserved while every publication has stable ids.

**2. Publications are an additive SDK capability.**

`Spatial.PluginSdk.Providers.IPublicationRegistry` (`ListAsync`,
`GetAsync`, `PutAsync`, `DeleteAsync`) over the records above. The phase-one
implementation is `Spatial.Provider.Publications`: declared entries from
config are immutable through the API, runtime entries live in a versioned
JSON file written atomically (temp file + replace) under a single-writer
lock. A PostGIS-backed registry for multi-instance deployments is a later
option, not phase one; the file keeps the database-free profile working and
does not couple metadata to feature storage. A name collision with a
declared entry is `invalid.arguments`.

**3. Bulk create-and-load is an additive SDK capability.**

`Spatial.PluginSdk.Providers.IDatasetIngest` creates a dataset and loads
every page in **one transaction** (the table exists only if every page
lands), with an identity mode:

```csharp
public enum IngestIdentity { None, Auto, Source }

public sealed record IngestRequest(
    string Dataset, int Srid,
    IngestIdentity Identity = IngestIdentity.Auto, string? IdentityField = null);
```

`Auto` gives the new table a database-generated identity primary key, so
uploaded layers are editable and lookup-able; `Source` uses a named integer
field; `None` loads a data-only table (query-only). PostGIS implements this
in `Spatial.Provider.PostGIS` alongside the existing faces. A provider that
cannot bulk-load simply does not implement the interface, in the
ADR-0037/0038 style.

**4. Foreign-format upload is adapter-owned and clarifies ADR-0020.**

Upload bodies are opaque bytes in a declared format, never a contract
payload. `Spatial.Interop.Ingest` (referencing `Spatial.Core` only, like
`Spatial.Interop.Esri`) decodes GeoJSON, newline-delimited GeoJSON and CSV
to canonical `FeatureBatch` pages **before** any SDK boundary is crossed.
JSON geometry therefore never enters core values or SDK contracts; this is
the same clarification ADR-0035 §2 made for the serving facade. Malformed
or unsupported input is a typed `invalid.arguments` and nothing partial is
returned. Streams and size/page caps bound resource use; `IAsyncEnumerable`
ingest is a later evolution, not phase one.

**5. The neutral host API is the product surface; Esri admin is a gated
projection.**

The engine exposes `GET/PUT/DELETE /api/publications[/{name}]` and
`POST /api/ingest`. The Esri admin surface (`/arcgis/admin`, provisional
route prefix from `Spatial:GeoServices:AdminRoot`) is a thin projection
onto the same registry and ingest capability, mounted **only when an admin
token is configured**, and targeting the *current ArcGIS REST admin API*
(not the v1.0 specification). Its supported/unsupported operation matrix is
a tested deliverable, not prose. The v1.0 corpus and the ArcGIS REST JS e2e
suite remain the serving proofs.

**6. Security is config-only and never request-supplied.**

Mutation routes require `Spatial:Admin:Token` (env `SPATIAL_ADMIN_TOKEN`),
compared in constant time; without it they are not mounted (neutral) or
return an actionable failure (Esri adapter). Uploads are bounded
(`MaxBytes`, `MaxFeatures`, format allowlist); the strict `schema.table`
grammar applies to dataset and publication names and is never concatenated
raw into SQL; the `{"url": ...}` remote-input form stays rejected (no
server-side fetch). Phase one adds no new error codes.

**7. Deferred decisions are recorded, not smuggled in.**

An ephemeral writable in-memory provider (so ingest/publish works without
PostGIS) and an `IFeatureEditStore` "omit identity on insert" mode are
in scope for the delivery plan but require their own ADRs before
implementation. The MapServer render boundary and the ImageServer raster
boundary are separate ADRs in their own plans.

## Consequences

- `Spatial.Core`, the canonical codecs and the existing SDK contracts are
  unchanged; the two new interfaces are additive.
- The GeoServices adapter's static `GeoServicesCatalog` snapshot becomes an
  async resolver over `IPublicationRegistry` + config; the catalog
  advertises runtime publications. Existing config-declared services keep
  working.
- New projects (`Spatial.Interop.Ingest`, `Spatial.Provider.Publications`)
  and their guard allowlist entries follow the normal rule: interface +
  implementation + tests + ADR together.
- The admin token is a new secret channel; it follows the ADR-0035 rule
  (config only, redacted, never in request bodies/logs) and the
  redaction tests are extended to the admin path.
- Ingest is a cancellable request with configured caps, not a job
  (ADR-0033). Large-file streaming and resumable upload are later work.
- Two follow-on ADRs are prerequisites for their phases: a writable
  in-memory provider for the database-free profile, and omit-identity-on-
  insert for editing uploaded layers. Neither blocks phase-one ingest +
  publish against PostGIS.
- MapServer/ImageServer publications reuse `PublicationKind` and the same
  registry/admin surface; their rendering and raster boundaries remain
  undecided by this ADR.

## References

- `architecture/publishing-and-ingest-plan.md` — delivery plan
- `architecture/map-service-plan.md`, `architecture/image-service-plan.md`
- `architecture/references/geoservices-compatibility.md`
- ADR-0020 (canonical binary interchange), ADR-0033 (in-process
  interfaces), ADR-0035 (GeoServices boundary adapter), ADR-0036 (granular
  capability interfaces), ADR-0037 (feature editing), ADR-0038
  (read-by-identity)
- `architecture/distilled/runtime.md`, `contracts.md`,
  `host-and-clients.md`
