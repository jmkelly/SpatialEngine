# Spatial.Esri.Codec

The Esri JSON codec: ArcGIS geometries and feature sets to and from core
values, over the canonical binary encodings the contract already owns. Two
records bind it: **ADR-0032** (the geometry contract faces and the
`Spatial.Core.Geometry.Codec` namespace) and **ADR-0029** (the feature-model
contract faces and `Spatial.Core.Features.Codec`). Route by task:
`architecture/principles.md`.

## Never

- No Esri object escapes this project: the codec's public surface is
  `Spatial.Core` values, and `Spatial.Esri.Json.*` types stay internal to the
  decode (ADR-0032).
- No change to a canonical binary layout without the codec version, the
  specification in `architecture/principles.md` and the round-trip tests
  landing together.
- No lossy decode reported as lossless: an Esri extent that cannot be
  represented is a typed refusal or a stated round-trip tolerance, never a
  silent narrowing.
- No spatial algorithm — this project translates bytes into values and back;
  buffer, union and projection live elsewhere (ADR-0033).
- No hand-written parse of a documented field: an unknown member is skipped
  by the decoder that owns the version, not special-cased here.

## Commands

- `dotnet test tests/unit/Spatial.Esri.Codec.Tests` — round trips, the
  multi-part and the empty-geometry cases.
- `dotnet test tests/unit/Spatial.Adapter.GeoServices.Tests` — the adapter
  that consumes these values.
- `eng/verify.sh --fast` — the lane a change here is handed off on.
