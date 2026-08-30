# Geometry Model

Read when designing or changing core geometry types, coordinate sequences or
interchange. See implementation-plan.md §7-8 and ADR-0001/0004/0020.

## Values

- `Coordinate` — readonly record struct: `X`, `Y`, optional `Z`, optional `M`.
  `null` means the ordinate is *absent*; `double.NaN` means *present but
  unknown*. A coordinate's layout is inferred from which ordinates are
  non-null.
- `CoordinateLayout` — `Xy`, `Xyz`, `Xym`, `Xyzm`. `OrdinateCount()`,
  `HasZ()`, `HasM()` and `Infer()` (never drops a present ordinate) are the
  structural helpers.
- `CoordinateReference` — authority + code identity only (ADR-0009),
  validated non-empty; `Epsg(int)` shorthand.
- `Envelope` — immutable, finite bounds, infinities represent emptiness
  (`Empty`). `default(Envelope)` is a degenerate envelope at the origin and is
  never "empty". Computed creators (`FromCoordinates`, `FromSequence`) skip
  NaN X/Y ordinates; explicit construction rejects NaN and infinite bounds.
- `ICoordinateSequence` — `Count`, `Layout`, `GetOrdinate(index, ordinate)`.
  Implementations: `PackedCoordinateSequence` (one `double[]` for the whole
  sequence — never one heap object per coordinate) and
  `ArrayCoordinateSequence` (coordinate array; reference implementation for
  small, construction-oriented uses).
- `IGeometry` — type, layout, CRS reference, empty state, coordinate count,
  computed envelope (structural, allowed).
- Simple-feature types: Point, LineString, Polygon, MultiPoint,
  MultiLineString, MultiPolygon, GeometryCollection. All immutable
  (ADR-0004); composite children are defensively copied.
- `GeometryFactory` — builders. Constructors are structural (they store what
  they are given); the factory normalises CRS across parts (see below).
- `GeometryTraversal` — `Parts()`, `DepthFirst()`, `Coordinates()`.
- `GeometryCodec` — canonical binary interchange, version 1 (spec below).
  `GeometryComparer` — structural equality and hashing.

## Semantics

### Layouts and packing

- Sequencing ordinates are X, Y, Z, M: in `Xym` the M ordinal lives at offset
  2; in `Xyzm` at offset 3.
- Packing a non-null ordinate into a layout that does not store it throws
  (no silent data loss); a null ordinate in a layout that stores it is
  packed as `double.NaN` (explicit missing value).
- This rule is format-neutral: future sequence backings (shared memory,
  mmap, WKB, Arrow) must keep the same geometry contracts.

### Composite rules

- **Layout merge ("most expressive wins")**: any Z raises to Xyz/Xyzm; any M
  raises to Xym/Xyzm, applied to X/Y/Z/M independently per part.
- **CRS normalization**: a composite carries at most one distinct non-null
  CRS. Parts without a CRS are unspecified; one distinct CRS wins; two
  conflicting CRSs are rejected by the factory (constructors stay
  structural). Encode/decode preserve per-part CRSs exactly.
- **Empty**: Point is empty without a coordinate; LineString with a zero
  count; Polygon when the exterior ring is empty; composites when they have
  no children or all children are empty. Empty geometries contribute no
  coordinates and have no envelope.
- **Point layout** is derived from its coordinate (empty → `Xy`); the codec
  and `CreateEmptyPoint` may carry an explicit layout for round-trip
  fidelity.
- Ring closure, orientation and validity are NOT enforced: validation is a
  plugin verb, not structural core behaviour.

### Equality

`GeometryComparer`/`IEquatable<T>` compare exactly: type, CRS, layout and
every coordinate, in order, across sequence backings (packed vs array).
Ordinate equality uses `double.Equals` so NaN values compare equal. Sequence
hashes hash layout plus every ordinate so equal sequences — even with
different backings — hash equal.

## Canonical binary format (v1)

Deterministic: structurally equal geometries encode to identical bytes;
nested layouts, CRSs, empties and NaN ordinates round-trip exactly. All
integers and doubles are little-endian. See `GeometryCodec` for the
authoritative description.

```
Header:
  byte[5]   magic "SGEOM"
  byte      format version (currently 1)

Node (recursive):
  byte      layout (CoordinateLayout value)
  byte      type (GeometryType value; values match classic WKB type numbers)
  byte      hasCrs (0 or 1)
  when hasCrs:
    int32   authority UTF-8 byte length, bytes
    int32   code UTF-8 byte length, bytes
  body by type:
    Point:              byte hasCoordinate (0 = empty, 1 = present); when
                        present, layout stride × 8 bytes
    LineString:         int32 count, count × stride doubles
    Polygon:            int32 ring count, ring count line string nodes
    MultiPoint / MultiLineString / MultiPolygon / GeometryCollection:
                        int32 element count, element count child nodes
```

The explicit `hasCoordinate` flag makes empty points unambiguous inside
collections (a naked zero-length body would be indistinguishable from
truncated input). Nesting is bounded by `MaxNestingDepth` (256) on both
encode and decode; element counts are validated against the remaining input
before allocation. Decode errors are actionable: `CanonicalFormatException`
(or `TryDecode`'s error out) carries the offending byte offset.

## Guards

- Round-trip, property (seeded random) and allocation tests live with the
  types: `tests/unit/Spatial.Core.Tests` (Phase 1).
- NetTopologySuite is absent from core dependencies (package allowlist,
  enforced by tests/architecture).