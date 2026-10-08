# Spatial.Tiling.Mvt

Mapbox Vector Tiles: the protobuf wire encoder and the tile request/response
over core-typed values. Two records bind it: **ADR-0070** (MVT is in scope;
protobuf and encoder types live here and not in a contract) and **ADR-0101** (a
tile with no extent on an axis is a typed rejection, never a narrowed one).
Route by task: `architecture/principles.md`.

## Never

- The `IVectorTileService` seam and its DTOs speak in core values; the
  protobuf types live here and never appear in one of those signatures
  (ADR-0070).
- No byte-layout decision taken from a web page or from memory: the golden
  fixtures in `tests/unit/Spatial.Tiling.Mvt.Tests` are the authority here, and
  a changed layout lands as a changed fixture in the same commit (ADR-0070).
- No narrowing a degenerate tile to make it renderable — reject it (ADR-0101).
- No reprojection, clipping or simplification invented here: geometry arrives
  already in the tile scheme, from the tile cache.
- No cache key that omits the content version, the scheme or the layer
  ordering.

## Commands

- `dotnet test tests/unit/Spatial.Tiling.Mvt.Tests` — encode, decode and the
  golden-byte fixtures.
- `dotnet test tests/unit/Spatial.Tiling.WebMercator.Tests` — the scheme the
  tiles are cut on.
- `eng/verify.sh --fast` — the lane a change here is handed off on.
