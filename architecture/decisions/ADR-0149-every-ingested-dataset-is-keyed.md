---
status: accepted
date: 2026-10-01
deciders: maintainer + agent
amends: ADR-0041, ADR-0140
summary: **Every ingested dataset is keyed**: `IngestIdentity.None` is removed from the contract rather than left as a request that builds a dataset which cannot name its own features, so an ingest always gives its dataset an identity column — the store's own (`Auto`) or a named integer field of the source (`Source`). The wire value `identity=none` is **refused by name** on both ingest routes (`POST /api/ingest`, `POST /arcgis/admin/uploads`) and by the CLI's `--identity`, with a message that says what to use instead, because a parameter is either honoured or rejected and never quietly ignored. What that buys is the whole per-feature face: an ingested layer's `OBJECTID` is its identity column, so `IFeatureLookup` answers (ADR-0140), `EsriObjectIdScheme.IsIdentity` is true, the dataset is editable (ADR-0037) and pushdown may run (ADR-0097). What it costs is a field the source did not supply, on every ingested dataset: `DescribeAsync` reports an `id` no client sent, and a schema that already has an `id` must name it (`Source`) rather than be loaded keylessly. `IDataCatalogue.CreateAsync` is deliberately **not** changed — ADR-0147 keeps a created dataset keyless in the contract's view, so the two create paths stay one keyed and one keyless, and each says why (amends 0041, completes 0140 §4's alternative).
---

# ADR-0149: Every ingested dataset is keyed

## Context

ADR-0140 decided that `IFeatureLookup.GetAsync` is refused with
`invalid.arguments` on a dataset that declares no identity column, and
recorded the alternative it did not adopt: give every dataset an identity
column at create/ingest time. The restriction it left in place is real. An
ingest with `identity=None` — the shape the `SpatialEngine-u2x.1` spike
measured on world-cities, 34,135 rows — produces a dataset with no durable
feature key, so:

- `IFeatureLookup.GetAsync` is refused (ADR-0140), and the adapter
  deliberately does not fall back to the Esri `OBJECTID` (ADR-0140 §3);
- `EsriObjectIdScheme.IsIdentity` is false, so the layer's `OBJECTID` is the
  1-based scan ordinal (ADR-0037) — a number a write renumbers and a
  restricted read would renumber (ADR-0097);
- therefore the layer is not editable (`MemoryDataset.Editable` is
  `IdColumns.Count > 0`, ADR-0037's gate), and no pushdown runs over it.

So a client-visible per-feature read was only available for identity-backed
layers, and the spike could not measure the face it was asked for on the layer
it was asked for.

Nothing in the engine required the keyless shape. `MemoryIngestPlan.Auto`,
`PostgisIngestPlan.Auto` and `SqlServerIngestPlan.Auto` already append one;
`None` existed because ADR-0041 listed it as a mode, not because a store
needed it. The bead this record closes asked which of the two levers to pull —
ingest keys the dataset, or `CreateAsync` does — and, either way, that the
decision is ingest's contract and needs its own record.

## Decision

**Every ingested dataset carries an identity column. `IngestIdentity.None` is
removed from the contract.**

### 1. The mode is gone, and the wire value is refused by name

The enum has two members, `Auto` and `Source`, and no third. Removing the
member is the strongest form of the rejection available to a C# contract: a
call cannot name the keyless mode at all, so a store's plan cannot grow a
branch that builds one.

A wire caller can still send the text, so both ingest entry points refuse it
by name and say what to use instead:

- `POST /api/ingest?identity=none` → `invalid.arguments` (400) naming `none`
  and offering `auto` or `source`;
- `POST /arcgis/admin/uploads?identity=none` → the Esri `400` envelope
  carrying the same message;
- `spatial dataset add --identity none` → a usage error, same rule.

That is the "honoured or rejected by name, never accepted and ignored" rule:
the old behaviour was neither honoured nor rejected, because the mode built a
dataset whose *consequences* (no lookup, no editing, a renumbering `OBJECTID`)
surfaced much later, at a face the caller had not asked about.

### 2. The plans always key, and their shapes say so

`PostgisIngestPlan.IdentityColumn` and `SqlServerIngestPlan.IdentityColumn`
were `string?` only so a keyless plan could exist; they are `string` now, and
`KeyColumns` is `[IdentityColumn]` rather than a mode-dependent expression. The
`CREATE TABLE` builders keep their two arms (`GENERATED ... AS IDENTITY
PRIMARY KEY` for `Auto`, `PRIMARY KEY (col)` for `Source`) and lose the
"neither" arm. `MemoryIngestPlan` does the same: `Materialise` always builds
the dataset with `[IdentityColumn]` as its id columns, and always re-keys the
stored features on the identity column (ADR-0119).

The cost is honest and is the trade this record makes: a dataset ingest
builds carries a field no client sent. `Auto` appends `id`, so `DescribeAsync`
reports one more column and `IngestOutcome.IdentityField` is always populated.
A source schema that *already* declares `id` is not loaded into a second one:
it is loaded with `Identity=Source&identityField=id`, which the plan already
required and still requires.

### 3. `CreateAsync` keeps building keyless datasets, on purpose

The other lever the bead named is deliberately not pulled. ADR-0147 gave a
created SQL Server table a clustered key the engine owns and then took it back
out of everything the contract reads, precisely so a created dataset keeps the
keyless shape ADR-0131's fixture and ADR-0140's refusal are written against.
Keying `CreateAsync` would undo that, and would do it for one path and not the
other: a `CreateAsync` sample batch and an ingest of the same document would
describe differently under one contract.

So the split is: **ingest keys, create does not**, and both are deliberate.
The consequence to state plainly is that ADR-0140's refusal still has a home —
a `CreateAsync`-built dataset, which is exactly what the postgis and SQL
Server integration suites exercise — and `IFeatureLookup` is still refused
there.

### 4. The in-repo callers follow the decision rather than the old default

The seeded datasets (Natural Earth, USGS earthquakes) declared
`identity: "none"`; they are `auto` now, so `eng/seed.sh` publishes layers
that are keyed, editable and lookup-able. The .NET CLI's `dataset add` rejects
`--identity none`, its starter project declares `auto`, and the workbench's
ingest form no longer offers the option.

## Consequences

- **The face the spike asked for exists on the layer it asked for.** An
  ingested layer's `OBJECTID` is store-derived, so `EsriObjectIdScheme.For`
  reports `IsIdentity`, editing is available, and `FeatureMatchPushdown` may
  push. `EsriObjectIdStabilityTests` and the Esri conformance suites answer
  over identity where they previously answered over the scan ordinal.
- **A stored schema grows a field.** Every ingested dataset's description
  carries an `id` the source did not have, and the served layer advertises it.
  A client that round-trips a layer's fields gets one back it did not send;
  that is the same shape `Auto` has always had, now universal.
- **`None` is a breaking contract change.** `IngestIdentity` loses a member,
  and a `Source`-schema that wants its own `id` keeps working. The engine is
  pre-1.0 and the mode existed for one release cycle; the alternative was to
  keep a mode that produces a dataset with no read-by-identity and no editing,
  which is the defect ADR-0140 documented.
- **`Auto` still refuses a colliding name.** An upload that already has a field
  called `id` is `invalid.arguments` naming it and pointing at `Source`, so
  no ingested dataset is refused for a name the contract reserved, and none
  silently drops the client's field.
- **Keyless datasets remain possible** — a `CreateAsync`-built one, or a table
  in a database the engine did not create — so the PostGIS and SQL Server
  lookup refusals keep their tests and keep their meaning.

## Alternatives

- **Leave `None` in the contract and stop honouring it.** An enum member no
  plan implements is a mode a caller can name and cannot get, which is the
  "accepted and ignored" shape the wall rules out.
- **Key `CreateAsync` as well.** Undoes ADR-0147's stated trade and makes the
  two create paths describe the same document differently. Rejected here;
  ADR-0147 is the record that says why a created dataset stays keyless.
- **Keep `None` for bulk reference data** (a large table nobody will ever read
  by id, where an `id` column is pure cost). The cost is one `bigint`
  identity column and one index; the benefit of the keyless shape is a layer
  with no per-feature read, no editing and no pushdown. Rejected: the cost is
  paid once and the loss is paid by every client.
- **Fail the ingest with `store.unavailable`.** The store is available; the
  mode is not a request the contract has. `invalid.arguments` names the
  request.

## References

- ADR-0041 (ingest and the identity modes — the mode list this record amends),
  ADR-0140 (the refusal this record removes the reason for, §4's alternative),
  ADR-0147 (a created dataset stays keyless in the contract's view),
  ADR-0037 (the identity gate on editing and the `OBJECTID` scheme),
  ADR-0038 (read-by-identity), ADR-0119 (`Feature.Id` is the identity
  column's value), ADR-0131 (the keyless created-dataset fixture),
  ADR-0092 (indexes on a created dataset), ADR-0097 (pushdown needs a
  store-derived key), ADR-0042 (the in-memory provider).
- `src/Spatial.Contracts/Providers/IDatasetIngest.cs`,
  `src/Spatial.Stores.Memory/MemoryIngestPlan.cs`,
  `src/Spatial.Stores.PostGIS/Core/PostgisIngestPlan.cs`,
  `src/Spatial.Stores.SqlServer/Core/SqlServerIngestPlan.cs`,
  `src/Spatial.Host/Api/IngestPipeline.cs` (`ParseIdentity`),
  `src/Spatial.Adapter.GeoServices/EsriAdminUploads.cs` (`IdentityOf`),
  `clients/dotnet/Spatial.Cli/Commands/DatasetCommands.cs`,
  `tools/seed/manifest.mjs`,
  `tests/integration/Spatial.Host.Tests/AdminEndpointTests.cs`
  (`An_ingest_asking_for_no_identity_is_refused_by_name`,
  `The_esri_admin_upload_asking_for_no_identity_is_refused_by_name`),
  `tests/unit/Spatial.Stores.Memory.Tests/MemoryStoreTests.cs`,
  `tests/unit/Spatial.Stores.PostGIS.Tests/PostgisIngestPlanTests.cs`,
  `tests/unit/Spatial.Stores.SqlServer.Tests/SqlServerQueryTests.cs`
  (each store's `Every_ingest_creates_a_keyed_dataset`).
