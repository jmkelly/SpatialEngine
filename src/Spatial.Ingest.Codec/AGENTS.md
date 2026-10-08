# Spatial.Ingest.Codec

The ingest decoders — GeoPackage, Shapefile, GeoJSON, Esri JSON — each
producing canonical `FeatureBatch` pages. Two records bind it: **ADR-0082**
(ingest honours the declared source CRS, reports what the decode did, and
streams pages into one transaction) and **ADR-0029** (the feature contract
faces and the `FeatureBatchCodec` namespace the pages are encoded through).
Route by task: `architecture/principles.md`.

## Never

- No decoder returning something other than `FeatureBatch` pages: the page
  shape is the contract every downstream store and tile cache assumes.
- No CRS assumed. A format that declares one is honoured, the transformation
  applied is reported in `IngestOutcome`, and a format that declares none is
  an explicit `DecodeOptions.Srid` (ADR-0082).
- No partial page set escaping: pages stream into the caller's transaction,
  and a decode failure leaves nothing behind (ADR-0082, ADR-0090).
- No spatial algorithm and no store concern — reprojection is a
  transformation, persistence is the store's (ADR-0033).
- No decoder reading a whole dataset into memory "because the file is small";
  the streaming path is the one that is tested.

## Commands

- `dotnet test tests/unit/Spatial.Ingest.Codec.Tests` — per-format decode,
  CRS handling and the streaming pages.
- `dotnet test tests/integration/Spatial.PostGIS.Tests` — the staged-then-
  loaded ingest end to end (ADR-0090).
- `eng/seed.sh` — a realistic dataset through this path.
