# Publishing & Ingest Implementation Plan

> **Status:** implemented (P0–P6 plus P4-sub, covered by unit, host and
> containerised PostGIS tests); P7 (clients & workbench UI) remains. Companion
> to
> `architecture/decisions/ADR-0041-ingest-and-publications-are-protocol-neutral.md`
> (the gating decision; **accepted**), ADR-0042, ADR-0043, ADR-0035, ADR-0037,
> ADR-0038 and the
> `architecture/distilled/*` digests. Read
> `architecture/references/geoservices-compatibility.md` first — it is the
> gap analysis this plan builds on.
>
> **Decisions settled** (previously open, recorded in §8): database-free
> ingest via an ephemeral in-memory provider (ADR-0042 before P2b); declared
> config services migrate to stable-id publications at startup; flat
> publication names (folders rejected in phase one); `PublicationKind` enum;
> ingest accepts both multipart and raw bodies; editing uploaded layers is
> in scope (ADR-0043 before P4-sub).
>
> **Goal:** a protocol-neutral way to **upload data into a dataset** and
> **publish datasets as a named service** at runtime, with the Esri admin
> API as one thin projection of it (not the model).
>
> **Related plans:** `map-service-plan.md`, `image-service-plan.md`
> (both reuse the publication registry and admin surface defined here).
>
> **Specification baseline:** the held Esri GeoServices REST Specification
> v1.0 does **not** define service creation. The Esri admin projection in
> §4.5 targets the *current* ArcGIS REST admin API (online docs, checked at
> implementation time), not the v1.0 PDF.

## 1. The problem

Two needs are currently unmet:

1. **Ingest.** There is no way to upload a data file and have it become an
   engine dataset. `POST /api/datasets` takes an SFBAT `batch` in the JSON
   body (a client must already speak canonical binary) and builds the table
   from the sample's schema. There is no decoder for GeoJSON/CSV/Shapefile,
   no HTTP upload, and no size/format policy.
2. **Publish.** `GeoServicesCatalog` is built once at startup from
   `Spatial:GeoServices:Services`, and each config entry maps a logical
   service to **one whole store** — every dataset in that store becomes a
   layer. A user cannot publish a single uploaded dataset under a stable
   service name and stable layer id without editing config and restarting.

Two additional gaps fall out of the same work:

- **Uploaded datasets are not editable.** `PostgisQueries.CreateTable`
  emits no primary key or identity column, so GeoServices editing
  (ADR-0037) cannot advertise capabilities and `IFeatureLookup`
  (ADR-0038) returns empty. "Upload → feature service" implies editing.
- **Layer ids are unstable.** The GeoServices plan already records this as
  open question S2: layer id is the index into the alphabetically sorted
  dataset list, so adding a dataset renumbers existing layers. A runtime
  publication needs persisted, stable ids.

## 2. Goal and non-goals

**Goal.** One neutral capability surface, two projections over it:

```text
                    ┌───────────────────────────────┐
  engine API ──────►│  IPublicationRegistry (SDK)   │◄──── GeoServices catalog
  /api/publications │  IDatasetIngest      (SDK)    │◄──── Esri admin adapter
  /api/ingest       └───────────┬───────────────────┘
                                │  (existing contracts)
                Spatial.Interop.Ingest ──► FeatureBatch ──► IDataCatalogue / IFeatureStore
                (GeoJSON, NDJSON, CSV)                └──► IDatasetIngest (PostGIS)
```

- **Track P — neutral.** Contracts + decoders + store capability + host
  admin API. This is the product feature; the workbench and SDKs use it.
- **Track P-Esri — projection.** ArcGIS admin routes mapped onto the same
  capability, for tooling that expects them.

**Non-goals (standing):**

- No new error codes in phase one (reuse `invalid.arguments`, `not.found`,
  `store.unavailable`); a `conflict` code is a later ADR if needed.
- No server-side fetch of caller-supplied URLs (the ADR-0035 SSRF rule).
- No job model (ADR-0033) — ingest is a cancellable request with a
  configured size cap, not a parked job.
- No portal/item model. The Esri projection is a *server admin* subset, not
  ArcGIS Online/Portal publishing.
- No changes to `Spatial.Core`, the canonical codecs, or the existing SDK
  contracts. All additions are additive capabilities (ADR-0036/0037/0038
  precedent).

## 3. Where it sits (boundaries)

| Concern | Project | References | Rule |
| --- | --- | --- | --- |
| `Publication`, `IPublicationRegistry`, `IDatasetIngest` | `Spatial.PluginSdk` | `Spatial.Core` only | Core-typed, no packages (guard test) |
| Format decoders → `FeatureBatch` pages | `Spatial.Interop.Ingest` (new) | `Spatial.Core` only | Same rule as `Spatial.Interop.Esri`; no HTTP |
| Durable publication registry | `Spatial.Provider.Publications` (new, provisional name) | Core + SDK | Config seed + JSON-file persistence |
| Bulk create+load | `Spatial.Provider.PostGIS` | Core + SDK | `IDatasetIngest` alongside the existing faces |
| Neutral admin REST | `Spatial.Host` | all | Composition + routes; thin handlers |
| Esri admin projection | `Spatial.Adapter.GeoServices` | SDK + `Spatial.Interop.Esri` | `/arcgis/admin`, gated |

New projects and packages require the guard allowlists in
`tests/architecture/Spatial.Architecture.Tests/ArchitectureGuardTests.cs`
to be updated **with the ADR first** (`PlatformProjectNames`,
`ImplementationProjectNames`, host `allowed`, `AllowedBoundaryReferences`,
`AllowedPackages`).

## 4. Core design (proposed decisions — to be recorded in ADR-0041)

### 4.1 The neutral concept: a *publication*

A **publication** is a named, ordered projection of datasets from one store
onto a protocol surface. It is deliberately not an Esri concept: the same
publication model carries `FeatureServer`, and later `MapServer` /
`ImageServer` (`PublicationKind`). One name = one protocol resource; one
ordered layer list = stable layer ids.

```csharp
public enum PublicationKind { Feature, Map, Image }          // Image/Map are later plans

public sealed record PublicationLayer(string Dataset, int LayerId, string? Name = null);

public sealed record Publication(
    string Name,
    PublicationKind Kind,
    string Store,                       // keyed engine store, e.g. "postgis"
    IReadOnlyList<PublicationLayer> Layers,
    string? Description = null,
    string? Copyright = null);
```

**Stable ids.** `LayerId` is assigned once (append-only, increasing) and
persisted. Removing a layer leaves a gap; ids are never reused or
renumbered. This closes S2. Config-declared services (the legacy
`Spatial:GeoServices:Services` path) keep the current sorted-index mapping
and are documented as legacy/unstable; runtime publications do not.

**No Esri types.** `Spatial.PluginSdk` gains no Esri or protocol type; the
adapter maps `Publication` → GeoServices JSON.

### 4.2 Contracts

```csharp
public interface IPublicationRegistry
{
    Task<IReadOnlyList<Publication>> ListAsync(CancellationToken cancellationToken = default);
    Task<Publication> GetAsync(string name, CancellationToken cancellationToken = default);
    Task<Publication> PutAsync(Publication publication, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}

public enum IngestIdentity { None, Auto, Source }

public sealed record IngestRequest(
    string Dataset, int Srid,
    IngestIdentity Identity = IngestIdentity.Auto,
    string? IdentityField = null);

public sealed record IngestOutcome(string Dataset, long Features, int Srid, string? IdentityField);

public interface IDatasetIngest
{
    /// <summary>Create a dataset and load every page in one transaction; the table exists only if all pages land.</summary>
    Task<IngestOutcome> IngestAsync(
        IngestRequest request, IReadOnlyList<FeatureBatch> pages, CancellationToken cancellationToken = default);
}
```

Both are **additive capabilities**, in the ADR-0037/0038 style: a provider
that cannot bulk-load simply does not implement `IDatasetIngest`, and the
host reports the store as ingest-incapable. `IReadOnlyList<FeatureBatch>` is
chosen for phase one to match the existing "in-memory pages" convention;
streaming ingest (`IAsyncEnumerable`) is a P8 evolution behind an ADR, with
the current cap as the safety valve.

**Why a new `IDatasetIngest` rather than `CreateAsync` + `WriteAsync`:**
the existing pair is not atomic (a failed write leaves a half-populated
table) and `CreateAsync` cannot accept a transaction handle. Bulk create +
load is a distinct capability with a distinct implementation (one
transaction, batched `INSERT … RETURNING`).

**Identity.** `IngestIdentity.Auto` makes the new table carry a
`GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY` so uploaded datasets are
editable (ADR-0037) and lookup-able (ADR-0038). `Source` uses a named
integer field from the data as the key; `None` loads a data-only table
(query-only). This directly addresses the missing-identity gap.

> **Dependency (ADR-0037 consequence).** Adds to an auto-identity table
> still require the client to supply an id. Full "client omits OBJECTID"
> editing needs the recorded `IFeatureEditStore` "omit identity on insert"
> revision. It is listed as P4-sub and is required before an uploaded
> service advertises `create`.

### 4.3 Decoders — `Spatial.Interop.Ingest`

Foreign-format code, Core-only, no HTTP, no algorithms:

| Format | Phase | Notes |
| --- | --- | --- |
| GeoJSON `FeatureCollection` | P1 | RFC 7946; SRID 4326; GeometryCollection handling |
| Newline-delimited GeoJSON | P1 | one `Feature` per line; streaming-friendly |
| CSV with `x,y`, `lon,lat` or `wkt` | P1 | type inference (string/int/double/bool/date); explicit `xField`/`yField` params |
| Esri `FeatureSet` (JSON) | P2 | reuse `Spatial.Interop.Esri`; gives the admin adapter a native payload |
| Shapefile / GeoPackage | P8+ | requires packages → its own ADR (allowlist) |

Decoders take a `Stream` and a target batch size, emit `FeatureBatch` pages,
and infer an `IFeatureSchema`. Malformed/unsupported input is a typed
`invalid.arguments`; nothing partial is returned. All geometry is converted
to core values **before** crossing any contract — this is the same rule
`Spatial.Provider.ArcGisRest` follows, so ADR-0020 is clarified, not
repealed (ADR-0041 must say so explicitly, mirroring ADR-0035 §2).

### 4.4 Persistence of publications

Phase one: `Spatial.Provider.Publications` composes

- **declared entries** from config (`Spatial:GeoServices:Services`, and a
  new neutral `Spatial:Publications:Declared` if desired) — immutable via
  the API; and
- **runtime entries** in a JSON file at `Spatial:Publications:Path`
  (default `./data/publications.json`), written atomically
  (`temp` file + `File.Move(overwrite: true)`) under a single-writer lock,
  with a schema `version` field.

A name collision with a declared entry is `invalid.arguments`. The file
format is a flat JSON array of `Publication`. Multi-instance deployments
require a shared backing store; a PostGIS-backed registry (`publications`
table) is a P8 option, not phase one. This keeps the Docker-free profile
working and avoids coupling metadata to the feature store.

### 4.5 API surfaces

**Neutral (host, product feature):**

```text
GET    /api/publications                     # Publication[]
GET    /api/publications/{name}              # Publication
PUT    /api/publications/{name}              # create/replace (idempotent)
DELETE /api/publications/{name}              # delete
POST   /api/ingest?store=&dataset=&srid=&format=&identity=&identityField=
                                             # body: file/raw upload -> IngestOutcome
```

`PUT` (not `POST`) for create keeps the neutral surface idempotent and
makes retries safe. Ingest is a single request: decode → create + load
(atomic) → return count. A convenience `publish` query parameter may
register a publication in the same call; if the second step fails the
response reports the partial state and is safely retryable (the dataset is
already loaded).

**Esri projection (adapter, gated):** mounted at
`Spatial:GeoServices:AdminRoot` (default `/arcgis/admin`), **only when an
admin token is configured**:

| ArcGIS admin route | Maps to |
| --- | --- |
| `GET /arcgis/admin/services` | `IPublicationRegistry.ListAsync` |
| `GET /arcgis/admin/services/{name}.{type}` | `GetAsync` |
| `POST .../{name}.{type}/createService` | `PutAsync` (service-definition subset) |
| `POST .../{name}.{type}/deleteService` | `DeleteAsync` |
| `POST /arcgis/admin/uploads` + `.../{id}/publish` | upload → `IDatasetIngest` → `PutAsync` |

Explicitly **unsupported** and rejected with the Esri error envelope:
data-store registration, `addToDefinition`/`updateDefinition`, portal
items, `gdbVersion`, and anything the v1.0 spec does not describe. The
supported/unsupported matrix is a tested deliverable, not prose.

### 4.6 Configuration

```jsonc
"Spatial": {
  "Admin": {
    "Token": ""                    // empty = all mutation routes disabled
  },
  "Publications": {
    "Path": "./data/publications.json"
  },
  "Ingest": {
    "MaxBytes": 104857600,         // 100 MiB
    "MaxFeatures": 1000000,
    "Formats": ["geojson", "ndjson", "csv"]
  }
}
```

`SPATIAL_ADMIN_TOKEN` is the environment fallback; it is a secret and
follows the ADR-0035 rule (config only, redacted, never in request bodies
or logs).

## 5. Phases

Each phase names its deliverables, its **proof** (the tests that make it
real) and its gate.

### P0 — Decision + contracts

- **Deliverable:** ADR-0041 records the neutral-capability decision, the
  ADR-0020 clarification (upload bodies are foreign-format ingress), the
  identity modes, and the Esri-admin-as-projection rule. Add the two SDK
  contracts and the `Publication`/`Ingest*` records.
- **Proof:** architecture guard tests updated (new projects, boundaries);
  SDK compiles with no packages.

### P1 — Decoders — **delivered**

- **Delivered:** `Spatial.Interop.Ingest` (`DatasetDecoder.Decode`,
  `IngestFormat.GeoJson`/`NewlineDelimitedGeoJson`/`Csv`, `DecodeOptions`,
  `DecodedDataset`, `IngestFormatException`) with schema inference, identity
  reporting and page batching; `tests/unit/Spatial.Interop.Ingest.Tests`
  (52 tests). GeoJSON covers all six simple-feature types plus
  `GeometryCollection`; CSV supports x/y, lon/lat, longitude/latitude and
  easting/northing pairs, explicit `XField`/`YField`, RFC 4180 quoting and
  type inference.
- **Deferred:** WKT and Esri `FeatureSet` payloads (P2); request size caps
  are the host's (P4); `IAsyncEnumerable` streaming (P8).
- **Proof:** round-trip fixtures; malformed input → `IngestFormatException`
  (mapped to `invalid.arguments` at the host); the interop architecture
  guard pins `Spatial.Core`-only references.

### P2 — Store ingest — **delivered**

- **Deliverable:** `PostgisIngestStore` (one transaction: `CREATE TABLE`
  with the identity option, batched inserts, rollback on failure).
- **P2b (scheduled):** a first-class, **ephemeral** writable in-memory
  provider keyed `memory` implementing the writable faces including
  `IDatasetIngest`, so ingest + publish works without PostGIS and the
  README's database-free start holds. New project + guard entries; ADR-0042
  is required before this phase.
- **Proof:** containerised PostGIS tests (create + ingest + count + scan;
  a mid-batch failure leaves no table); unit tests for the SQL leaves;
  P2b gets unit tests plus a host HTTP test that the `memory` store is
  always available and non-durable by design.

### P3 — Publication registry — **delivered**

- **Deliverable:** `Spatial.Provider.Publications` (declared + persisted,
  atomic write, name-collision rejection).
- **Proof:** unit tests for CRUD, atomic replace, restart persistence,
  collision, and corrupt-file diagnostics.

### P4 — Neutral host admin API — **delivered**

- **Deliverable:** `/api/publications` CRUD and `/api/ingest`; admin token
  gate; limits; OpenAPI; SDK wire types.
- **Proof:** host integration tests — 401/403 without/with token, size and
  format limits, disable-when-unconfigured, ingest → publish → query
  end-to-end through the engine API.
- **P4-sub (committed):** `IFeatureEditStore` omit-identity-on-insert so
  an uploaded auto-identity layer advertises `create`. ADR-0043 is required
  before this work; it must decide the mechanism (a sentinel identity, an
  explicit mode on `AddAsync`, or a sibling method) because `FeatureId` is
  currently a non-empty string and `Feature` has no unassigned state.

### P5 — GeoServices serves the registry — **delivered**

- **Deliverable:** replace the static `GeoServicesCatalog` snapshot with an
  async resolver over `IPublicationRegistry` + config; the catalog
  advertises runtime publications; FeatureServer/layer/query/edit resolve
  by `PublicationKind.Feature` and persisted `LayerId`; config services
  keep working unchanged.
- **Proof:** existing GeoServices unit/HTTP suites stay green; a new test
  creates a publication at runtime and reads it through the adapter;
  ArcGIS REST JS e2e (`clients/typescript/test/geoservices-e2e.test.ts`)
  against a runtime-created service.

### P6 — Esri admin projection — **delivered**

- **Deliverable:** `/arcgis/admin` route group (token-gated) with the
  §4.5 matrix; uploads staging with TTL and size caps; Esri error
  envelope.
- **Proof:** HTTP tests per route; the unsupported matrix tested;
  token required; no route mounted without config.

### P7 — Clients & workbench

- **Deliverable:** TS + .NET SDK methods; a workbench **Data** screen
  (choose file → format/SRID/identity → target store → publish → result);
  progress and typed errors.
- **Proof:** `eng/e2e-web.sh` and `eng/workbench-e2e.sh` extended to
  upload and publish from the real client paths.

### P8 — Scale-out and format expansion (post-MVP)

- PostGIS-backed registry + optimistic concurrency (`ETag`/revision);
  streaming ingest; Shapefile/GPKG behind an ADR; chunked/resumable
  upload; optional `conflict` error code.

## 6. Security, limits, failure modes

- **Auth.** All mutation routes (neutral and Esri) require
  `Spatial:Admin:Token`; Esri requests may present `token` or
  `Authorization: Bearer`; compare in constant time. Unset token ⇒ routes
  are not mounted (neutral) / return an actionable 503 (Esri adapter), and
  read-only serving is unaffected.
- **Limits.** `MaxBytes`, `MaxFeatures`, a format allowlist, and page-size
  bounds. Over-limit is `invalid.arguments` (400), never a truncated load.
- **No SSRF.** Upload bodies are data, never fetched; the `{"url": ...}`
  form stays rejected.
- **Identifiers.** Reuse the strict `schema.table` grammar (`[a-z_][a-z0-9_]*`,
  `public` default); never concatenate raw text into SQL. Publication names
  follow the same grammar.
- **Atomicity.** Dataset create+load is one transaction; publication
  registration is one atomic file write. A combined ingest+publish reports
  which half succeeded and is retryable.
- **Redaction.** No token, connection string or file path in errors/logs;
  reuse the store redaction contract and add an assertion for the admin
  path.
- **Resource safety.** Uploads are decoded to core values and written;
  no temp file is required in the common path, and any staging file has a
  TTL and a size cap.

## 7. ADR prerequisites

| ADR | Decision | Blocked phases | Status |
| --- | --- | --- | --- |
| **ADR-0041** | Ingest and publications are protocol-neutral additive SDK capabilities; upload bodies are foreign-format ingress (ADR-0020 clarification); identity modes; Esri admin is a gated projection targeting the current admin API, not the v1.0 spec | P0 (all) | accepted |
| **ADR-0042** | An ephemeral writable in-memory provider implements the writable faces, enabling database-free ingest/publish | P2b | accepted |
| **ADR-0043** | `IFeatureEditStore` may omit identity on insert | P4-sub | accepted |
| Map/Image ADRs | see `map-service-plan.md` / `image-service-plan.md` | P5 for non-Feature kinds | later |

Numbering after 0041 is provisional (0040 was the last issued ADR before
this plan).

## 8. Decisions (settled)

1. **Database-free ingest: yes.** Add a first-class, ephemeral writable
   in-memory provider (keyed `memory`) so the workbench upload → publish
   demo runs without PostGIS, matching the README's "start without a
   database". It is explicitly non-durable and development-oriented; ADR-0042
   precedes the phase.
2. **Declared services migrate to publications at startup.** Config shape
   is preserved (`Spatial:GeoServices:Services`), but each declared entry is
   projected to a `Publication` at composition and gets stable layer ids, so
   declared and runtime services behave identically. The legacy sorted-index
   mapping is removed.
3. **Publication names are flat.** Esri folder syntax (`Folder/Service`) is
   rejected in phase one (with a typed error) and recorded as a limitation.
4. **`PublicationKind` stays an enum.** Three planned kinds are sufficient;
   an OGC API Features binding, if ever added, is a new enum member plus an
   ADR, not a redesign around capability strings.
5. **Ingest accepts both multipart and raw bodies.** Raw
   (`application/octet-stream` + query parameters) is the streaming-friendly
   path; multipart is the documented browser/tool path. Both share one
   decoder entry point.
6. **Editing uploaded layers is in scope.** Commit to the
   omit-identity-on-insert follow-up (P4-sub) so an uploaded auto-identity
   layer advertises `create`; ADR-0043 decides the mechanism.

**Still to be measured, not decided by preference:** the PostGIS-backed
registry (P8) waits for a real multi-instance need; Shapefile/GPKG decoders
(P8) wait for package/ADR approval; large-file streaming waits for a
measured cap problem.

## 9. Quality and delivery gates

The repository's deterministic gates apply unchanged (`AGENTS.md`):
zero build warnings, coverage floor, CRAP < 10, metrics (`failOn:
moderate`). New code should follow the existing factoring pattern — thin
endpoints over named engines (as `FeatureQueryEngine` / `FeatureEditEngine`
did) — so complexity stays under the CRAP and metrics thresholds. The full
`eng/verify.sh` plus the Docker/PostGIS and JavaScript suites are required
before done.

## 10. References

- `architecture/references/geoservices-compatibility.md` — gap analysis
- `architecture/geoservices-implementation-plan.md` — GeoServices serving/consuming plan (S2 layer-id open question)
- ADR-0020 (canonical binary), ADR-0033 (in-process interfaces), ADR-0035 (GeoServices boundary), ADR-0036 (granular capability interfaces), ADR-0037 (editing), ADR-0038 (read-by-identity)
- `architecture/distilled/contracts.md`, `runtime.md`, `host-and-clients.md`
