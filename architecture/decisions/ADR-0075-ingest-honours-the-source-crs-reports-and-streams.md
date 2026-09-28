---
status: accepted
date: 2026-09-27
deciders: maintainer + agent
amends: ADR-0041
---

# ADR-0075: Ingest honours the source CRS, reports the decode, and streams

## Context

ADR-0041 §4 made ingest a real feature with a thin edge, in three specific
places. The breadth was never the problem: three formats decode to canonical
`FeatureBatch` pages. These were the shallowness:

1. **The source CRS was never consulted.** `DecodeOptions.Srid` was stamped on
   every decoded geometry and the doc comment said why: *"the source format's
   own CRS is not consulted because the engine's CRS identity is explicit and
   curated (ADR-0009)"*. The argument is sound — CRS *definitions* are a
   plugin concern (ADR-0009) — but it was applied to the *declaration* a
   document carries about itself. GeoJSON has had a standard place for that
   since 2008, and every producer with projected data emits one. So a file
   declaring `EPSG:3857` decoded to Web Mercator metres labelled `EPSG:4326`
   and loaded without an error. A reproduction test put `1113194` into a
   column it also labelled `EPSG:4326`: a ~100× displacement with a `200 OK`.

2. **Nothing said what the decode did.** The reference documentation already
   said malformed features are dropped. Nothing observable said so: the decode
   returned pages, the caller could not tell a clean load from one that
   quietly lost rows, and a `null` in the first row typed a column as a string
   with no way to see why.

3. **The whole upload was materialised.** `DecodedDataset` carried
   `IReadOnlyList<FeatureBatch>`, every page of every feature in memory before
   the first write — because `JsonDocument.Parse(Stream)` materialises the
   document, and ND-GeoJSON and CSV read to the end. A 500 MB upload was a
   500 MB allocation, and a malformed row in the last page failed a job that
   had already done all the work.

ADR-0041 §4 said *"IAsyncEnumerable ingest is a later evolution, not phase
one"*. This is that evolution, and the first two items are the honest-CRS
half of the same change: reprojection belongs to the decode, because the
decode is the only thing that has read the document's declaration.

The host was already reprojecting — but as a *second pass* over the decoded
pages, in `IngestPipeline.ConvertIfNeeded`, told which CRS the source was in by
the `sourceSrid` query parameter. That is the assumption this ADR removes. The
pass is deleted rather than kept as a fallback: two places that can both
reproject is how they end up disagreeing.

## Decision

**1. A declared source CRS is honoured, and a contradiction is a failure.**

The codec reads the declaration where the format has one — the GeoJSON 2008
`crs` member, the first record of newline-delimited GeoJSON — and the
resolution accepts the spellings producers actually write (`EPSG:4326`,
`urn:ogc:def:crs:EPSG::3857`, the versioned URN, the OGC HTTP URL, `CRS84`).
CSV declares no CRS, so the codec adopts the convention line-oriented
geospatial text already uses: a leading comment directive, `# crs=…`, before
the header row. Adding a format here is out of scope; adding a one-line
convention to an existing one is not.

The source CRS is then the declared one, and the decode reprojects into
`DecodeOptions.Srid` as it reads. Three cases are a typed `invalid.arguments`
rather than a guess:

- a declared CRS that is not an EPSG code (the authority is checked, not just
  the trailing digits — `…crs:ESRI:102100` ends in digits and is not EPSG:100);
- a declared CRS the decode cannot transform because no reprojector was
  supplied, rather than stamping the target CRS on unreprojected coordinates;
- a declaration that **contradicts** an asserted `sourceSrid`. One of the two
  is wrong and the engine cannot tell which, so it says so instead of picking
  a winner.

A declaration that appears *after* the features it describes is rejected for
the same reason: the coordinates are already stamped and cannot be
retrospectively transformed.

**2. Reprojection moves into the decode, through a codec-owned seam.**

`Spatial.Ingest.Codec` references `Spatial.Core` only, so it cannot see
`ICoordinateTransforms` and does not try. It declares the one operation it
needs:

```csharp
public interface IIngestReprojection
{
    IGeometry Reproject(IGeometry geometry, string source, string target, CancellationToken cancellationToken = default);
}
```

The host adapts the engine's transform service onto it. Algorithms stay in
the transform plugin (ADR-0009, ADR-0002); only the *decision* to reproject
belongs to the decode. `IngestPipeline.ConvertIfNeeded` is deleted.

**3. A decode is reported, not assumed.**

`DecodeReport` (`Spatial.Core.Features.Ingest`, core-typed so it can cross a
contract) records what was read, what was dropped and why with a typed
`IngestSkipReason`, the inferred attribute kinds *with the evidence for each*
(`InferredField.Observed` and its null count), the final schema, and what
happened to the CRS. `IngestOutcome` carries it, so `POST /api/ingest`
returns it.

The skip list is bounded at 100 entries with an exact `SkippedCount`
alongside: a report that grows with the damage is its own denial of service.
Dropping records is opt-in (`DecodeOptions.SkipMalformed`, config
`Spatial:Ingest:SkipMalformed`, default off) because a bad row in a large
upload should not become a load with a silent hole in it; the report is what
makes a deliberate choice visible.

**4. Decode streams, and ingest loads the stream.**

`DatasetDecoder.DecodeStreamingAsync` returns a `DecodeSession`: an
`IAsyncEnumerable<FeatureBatch>` whose schema is known *before* the first
page, plus a `Task<DecodeReport>` that completes when the stream is drained.
GeoJSON is read through a `Utf8JsonReader` over a compacting buffer, so a
`FeatureCollection`'s array is walked one feature at a time and the document
is never resident; ND-GeoJSON is read line by line and CSV through a
pull-based RFC 4180 state machine.

`IDatasetIngestStream` is the store-facing sibling of `IDatasetIngest`: the
same atomic create-and-load, pages arriving as they are decoded. It takes the
schema up front precisely because the table is created before the first page
lands. Implemented by the in-memory, PostGIS and SQL Server providers; the
feature cap is enforced *while* streaming, or it is not a cap.

A separate face, not an overload, for the ADR-0033 reason every other
capability is one: a store implements the one it can, and the buffered face
remains correct for an upload already in memory. The host uses the streaming
face when the store offers it and the buffered one otherwise.

**5. A streamed schema is inferred from a bounded prefix, and says so.**

The schema has to be fixed before the first page, so inference reads at most
`InferSampleSize` records (default the batch size). Records read before the
session existed are emitted first, not dropped — a sample is a way to learn
the schema, not a way to lose rows. A record outside the sample carrying an
attribute the inferred schema has no field for is dropped and reported as
`FieldNotInferred`; it is never silently narrowed. `DecodeReport` carries
`Sampled` and `SchemaSampled` so a caller can tell a schema inferred from the
whole document from one inferred from a prefix.

## Consequences

- `Spatial.Core` gains five small reporting types under
  `Features.Ingest`; `Spatial.Contracts` gains `IDatasetIngestStream` and one
  optional field on `IngestOutcome`. Both are additive (ADR-0033, ADR-0041
  §3). `Spatial.Contracts` still takes only a `Spatial.Core` reference and no
  packages.
- Every store implementing `IDatasetIngest` now also implements
  `IDatasetIngestStream`; a store that cannot stream pages simply would not,
  and the host falls back. The feature cap is checked during the stream, so
  the two host paths needed their message unified.
- Streaming decode costs a fixed 64 KiB buffer per decode plus one page, and
  grows the buffer only when a single JSON value is larger than that. The
  bounded-memory claim is asserted by peak live memory, measured against the
  buffered path on the same document (10 MB against 33 MB for 40 000
  features), not by a per-page intuition.
- The codec is now correct on a stream that delivers a byte at a time, which
  is what an HTTP request body is. A reader span that reached past the valid
  data read stale pooled bytes, which a `MemoryStream` never exposed.
- CSV gains the `# crs=` directive. An upload that declares a CRS its engine
  cannot resolve now fails where it previously loaded mislabelled — that is
  the point, but it is a behaviour change for a caller who was relying on the
  old silence.
- `eng/seed.sh` is unchanged: its sources declare `sourceSrid` and are
  reprojected, now inside the decode. A source whose file *also* declares a
  CRS contradicting `sourceSrid` would now fail; none do.
- Upload bodies remain opaque bytes decoded before any contract boundary
  (ADR-0041 §4); no JSON geometry enters core values or SDK contracts.

## References

- `architecture/publishing-and-ingest-plan.md` — the phase-one plan this completes
- `architecture/distilled/contracts.md`, `host-and-clients.md`, `plugins.md`
- ADR-0041 (ingest and publications), ADR-0009 (CRS identity is explicit),
  ADR-0023 (bounded streams), ADR-0029 (feature model faces), ADR-0033
  (in-process interfaces), ADR-0074 (query plan, whose read-side streaming
  alternative this does not take: a cursor on a page is enough for a read,
  but an upload has to be written)
