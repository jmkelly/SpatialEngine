# Core: Geometry, Features, Codecs (distilled)

Covers `Spatial.Core` only. Implements ADR-0001/0004/0009/0020/0029/0032.

## The inclusion test (all three must hold)

1. Almost every spatial plugin must exchange it.
2. Independently developed plugins must agree on its meaning.
3. Representable without choosing a spatial algorithm.

**Core owns:** spatial values (geometry, features, schemas, CRS identity) and
canonical encoding/decoding. Runtime behaviour — service interfaces and their
in-process composition — lives in `Spatial.Contracts` and `Spatial.Host`
(see `runtime.md`).

**Core never implements:** buffer, intersection, union, difference,
predicates, distance, area, length, centroid, simplification, validation,
coordinate transformation, spatial indexing, SQL/store behaviour, rendering,
tiling, project persistence, workflow, desktop-shell behaviour.

**Permitted structural behaviour only:** type/empty inspection, coordinate
enumeration/counts, layout inspection, traversal, envelope from minima/maxima,
canonical encode/decode, structural equality.

**Hard guard:** `Spatial.Core` has zero packages, zero project references
(test `Core_has_no_dependencies`).

## Geometry values

| Type | Essence |
| --- | --- |
| `Coordinate` | readonly struct; X/Y/Z/M. `null` = ordinate absent; `NaN` = present but unknown. |
| `CoordinateLayout` | `Xy/Xyz/Xym/Xyzm`; `Infer()` never drops a present ordinate. |
| `CoordinateReference` | authority + code only (e.g. `EPSG:4326`); identity, never math. |
| `Envelope` | immutable, finite; infinities mean empty; `default` is degenerate, NOT empty. |
| `ICoordinateSequence` | `Count`, `Layout`, `GetOrdinate(i, ord)`. Backings: `PackedCoordinateSequence` (one `double[]`), `ArrayCoordinateSequence` (reference impl). |
| Shape types | Point, LineString, Polygon, MultiPoint, MultiLineString, MultiPolygon, GeometryCollection — all immutable, composite children defensively copied. |
| `GeometryFactory` | creation; normalises CRS across parts. |
| `GeometryTraversal` | `Parts()`, `DepthFirst()`, `Coordinates()`. |
| `GeometryComparer` | exact structural equality/hash: type, CRS, layout, every coordinate in order; `double.Equals` so NaN == NaN. |
| `GeometryCodec` | canonical binary v1; namespace `Spatial.Core.Geometry.Codec`. |

Contract faces (ADR-0032): bind `IGeometry`, `ICoordinateSequence`,
`IPoint`…`IMultiPolygon`, `IGeometryParts`, `IGeometryFactory` when only
inspecting/transporting; construct with concrete types.

### Semantic rules (memorise these)

- Ordinate order is X, Y, Z, M — in `Xym`, M sits at offset 2.
- Packing a non-null ordinate into a layout that doesn't store it **throws** (no silent data loss); null ordinate in a storing layout packs as `NaN`.
- Composite layout merge: "most expressive wins" per part (any Z → Xyz/Xyzm; any M → Xym/Xyzm).
- Composite CRS: parts without CRS are unspecified; one distinct CRS wins; two conflicting CRSs are **rejected by the factory**.
- Empty: Point w/o coordinate; LineString w/ zero count; Polygon w/ empty exterior; composite with no (non-empty) children. Empties contribute no coordinates, no envelope.
- Ring closure, orientation, validity are **NOT enforced** — validation is a plugin verb.
- Equality is exact & structural; NaN compares equal (so equal values encode to identical bytes).

## Feature values (namespace `Spatial.Core.Features`)

| Type | Essence |
| --- | --- |
| `AttributeKind` | `Null(0) | Boolean | Int64 | Double | String | Geometry | DateTimeOffset | Guid` — stable wire byte values. |
| `AttributeValue` | boxing-free tagged union; default is `Null`. |
| `FieldDefinition` | name, kind (never `Null`), nullable, optional description. |
| `FeatureSchema` | ordered fields, ordinal-unique names; content equality incl. descriptions. |
| `FeatureId` | non-empty string. |
| `Feature` | id + schema + one attribute per field; validated (count, kind, nullability) at construction. |
| `FeatureBatch` | features sharing exactly the batch schema. |
| `FeatureBatchCodec` | canonical binary v1; namespace `Spatial.Core.Features.Codec`. |

Contract faces (ADR-0029): `IFeature`, `IFeatureSchema`, `IFeatureBatch`,
`IFieldDefinition` — bind these in contract-side code.

### Feature semantics

- Absence = field `nullable` flag + null marker on the wire, never a payload; null only legal in a nullable field (enforced at construction and decode).
- Equality: doubles via `double.Equals`; strings **ordinal**; geometries via `GeometryComparer`; DateTimeOffset by **UTC ticks + offset** (stricter than BCL — same-instant-different-offset is NOT equal).
- Schema compatibility = append-only prefix rule (`IsDecodableFrom`): writer's fields must start with exactly the reader's fields, in order, matching names/kinds/nullability. Reorder/rename/retype/nullability-narrowing are incompatible. `FeatureBatchCodec.Decode(data, targetSchema)` projects to any decodable prefix.

## Canonical binary formats (v1, little-endian, deterministic)

Equal values encode to identical bytes; malformed input → exception/error with
the offending **byte offset**. Counts validated against remaining input before
allocation; nesting bounded (`MaxNestingDepth` 256 for geometry).

- Geometry: magic `"SGEOM"`, version byte; per node: layout byte, type byte
  (WKB-compatible numbers), hasCrs + authority/code strings, body per type;
  Point carries an explicit `hasCoordinate` flag so empty points are
  unambiguous in collections.
- Feature batch: magic `"SFBAT"`, version byte; schema (fields: name, kind,
  nullable, hasDescription?); features (id, then per field: isNull flag +
  payload by kind). Geometry payload = SGEOM bytes, byte-for-byte. DateTimeOffset
  = int64 UTC ticks + int16 offset minutes (validated range ±14h). Guid = 16
  bytes. Trailing bytes rejected. Flag bytes must be 0 or 1.

## Where tests live

Round-trip, seeded-property, malformed-input, determinism and allocation
tests live with the types in `tests/unit/Spatial.Core.Tests` (Phases 1–2).
NTS absence from core is enforced by the package allowlist.
