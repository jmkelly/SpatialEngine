---
status: accepted
date: 2026-09-14
deciders: maintainer + agent
---

# ADR-0068: ImageServer authored metadata — service XML on the map, per-item XML on the catalog item

## Context

T-042 (ADR-0054) served the service-level `metadata` resource as a JSON
dataset projection (name, description, extent, band count/type, stats,
copyright) with an explicit documented divergence: the engine kept no
authored (ISO/FGDC) metadata store, so byte-fidelity with the reference
was impossible, and the per-item `{rasterId}/metadata` resource did not
exist at all. The Esri reference (S3 `metadata/` and `raster-metadata/`,
task T-056) returns the authored ISO/FGDC XML document at both levels.

Serving a derived JSON projection where clients expect the authored
document is a silent shape lie: a client fetching `metadata` for lineage
or compliance gets engine-synthesised fields instead of the publisher's
record, and per-item lineage has no resource at all.

## Decision

Author metadata as XML on the existing publication models, then serve the
bytes verbatim as `application/xml` at both levels, with failures as
structured codes (`invalid.arguments`, `not.found`,
`store.unavailable`):

1. **Service-level `metadata`** — a new optional `MetadataXml` on the
   `Map` contract (core `string`, so the SDK wall is untouched),
   authored through the admin `PUT /api/maps/{name}`
   (`metadataXml`), declared maps (`Spatial:Maps:Declared:…:MetadataXml`)
   and persisted in `maps.json`. The endpoint serves the bytes with
   `Results.Text(xml, "application/xml")`; a map without authoring
   answers a typed `not.found` instead of an invented document. Only an
   absent format or `f=xml` passes (`EsriFormat.EnsureXml`); anything
   else — including the old `f=json` — is `invalid.arguments` naming
   the XML surface. This **replaces** the ADR-0054 JSON projection and
   its `EsriImageMetadata` shape.
2. **Per-item `{rasterId}/metadata`** — a new optional `MetadataXml` on
   the `RasterCatalogItem` contract and the provider-side
   `RasterCatalogItemDescriptor`, authored per catalog item through
   `Spatial:Raster:Sources:…:Items:…:MetadataXml` and served
   byte-faithful as `application/xml`. An item without authoring is a
   typed `not.found`; an unknown id or a service without a catalog keeps
   the existing `not.found`/`invalid.arguments` failures. The route
   sits beside `{rasterId}/thumbnail` and takes the same
   GET+POST + `f=xml` convention.
3. **Fail fast at authoring** — a non-empty document must be
   well-formed XML where it is stored: `MapValidator` rejects a bad
   service document with `invalid.arguments` on `PutAsync` (and on
   declared-map seeding, including whole-store maps, which otherwise
   skip normalisation), and `VipsRasterCatalogue` rejects a bad item
   document at construction, following the ADR-0054 attribute-table
   precedent. Documents are never parsed at request time.
4. **Cancellation flows end to end** — both endpoints pass the request
   token through parameter reading, service resolution and the
   catalogue calls (`DescribeAsync`/`ListItemsAsync`); HTTP maps a
   cancelled token to 499 via the existing mapper.

`Directory.Packages.props` is untouched (validation uses BCL
`System.Xml` only); renderer, NetVips and store types stay inside
their owning implementations.

## Consequences

- The T-042 `metadata?f=json` JSON projection is gone; clients holding
   the old shape get a 400 naming `f=xml` for a format, or a 404 for a
   service whose map carries no authored document. The map admin
   surface gains one optional field; existing maps are unaffected
   (`null` means "no authored metadata").
- Root output and every capability flag are unchanged; T-043 owns flag
   honesty and is untouched.
- New SDK surface is core-typed only (`string` on `Map` and
   `RasterCatalogItem`); the provider descriptor gains the matching
   optional, and host raster-item config gains `MetadataXml`.
- Success, failure (missing authoring, unknown id, bad format,
   malformed authoring) and cancellation are tested at unit and
   integration level.
- Follow-ups deliberately left out: generating ISO from `RasterInfo`
   (invented lineage is worse than an honest 404), attachment-style
   metadata uploads, and OGC metadata harvesting of the authored
   documents.

## Alternatives

- **Keep the JSON projection and add XML beside it** (`f=json` →
   JSON, absent/`f=xml` → XML): rejected — two sources of truth for
   one resource, and the JSON projection is exactly the divergence
   T-056 was filed to remove.
- **Derive per-item XML from `RasterInfo`** (extent, bands, stats as
   synthesised ISO): rejected — the engine cannot honestly mint a
   lineage record; an honest 404 beats a fabricated document.
- **Author per-item metadata on the map layer**: rejected — catalog
   items are rows of the raster dataset, not map layers; the item is
   authored where items are authored (the raster source config).
