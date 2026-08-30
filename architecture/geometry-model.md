# Geometry Model

Read when designing or changing core geometry types, coordinate sequences or
interchange. See implementation-plan.md §7-8 and ADR-0001/0004/0020.

## Values

- `Coordinate` — readonly record struct: X, Y, optional Z, optional M.
- `CoordinateReference` — authority + code identity only (ADR-0009).
- `ICoordinateSequence` — `Count`, `Layout`, `GetOrdinate(index, ordinate)`;
  initial implementations: packed double sequence, array sequence.
- `IGeometry` — type, layout, CRS reference, empty state, coordinate count,
  computed envelope (structural, allowed).
- Simple-feature types: Point, LineString, Polygon, MultiPoint,
  MultiLineString, MultiPolygon, GeometryCollection.
- All geometry values are immutable (ADR-0004).

## Design rules

- No heap object per coordinate in large sequences (packed doubles).
- Future sequence backings (shared memory, mmap, WKB, Arrow) must not change
  the geometry contracts.
- Builders produce new values; no in-place mutation.
- Binary round trips are canonical and versioned (ADR-0020).

## Guards

- Round-trip and allocation tests land with the types (Phase 1, tests/unit).
- NetTopologySuite is absent from core dependencies (package allowlist).