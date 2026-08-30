# Feature Model

Read when designing or changing feature values, schemas or batch
interchange. See implementation-plan.md §6.1, §8 and ADR-0001/0004/0020.

## Values

All types live in `Spatial.Core.Features`, `src/Spatial.Core/Features/`, zero
dependencies (enforced by tests/architecture).

- `AttributeKind` — the tagged-union kind: `Null | Boolean | Int64 | Double |
  String | Geometry | DateTimeOffset | Guid` (explicit byte values; the wire
  format is stable).
- `AttributeValue` — a **boxing-free** tagged union: booleans, int64s,
  doubles, date-times and GUIDs are stored in dedicated value-typed fields;
  strings and geometries are already references. No attribute value allocates
  a heap object. The default value is `Null`.
- `FieldDefinition` — name, kind (never `Null`), nullability, optional
  description. Equality includes the description.
- `FeatureSchema` — ordered fields, ordinal-unique names. Equality is
  content-based and includes descriptions.
- `FeatureId` — a non-empty string.
- `Feature` — id + schema + one attribute per field, defensively copied and
  validated (count, kind, nullability) at construction.
- `FeatureBatch` — features sharing **exactly** the batch schema (content
  equality; an equal-but-distinct `FeatureSchema` instance is accepted).
- `FeatureBatchCodec` — canonical binary batch interchange, version 1
  (spec below).

## Semantics

### Nullability and kinds

- A field's kind is never `AttributeKind.Null`; absence is expressed by the
  field's `nullable` flag and encoded as a null marker, never as a payload.
- A null value is legal only in a nullable field, enforced at `Feature`
  construction and at decode (`Feature` ctor) and read side (marker check).

### Attribute-value equality

Exact and structural, matching the geometry model:

- Doubles use `double.Equals` — NaN compares equal, +0.0 equals −0.0.
- Strings compare **ordinally** (case-sensitive, culture-insensitive).
- Geometries compare through `GeometryComparer`.
- Date-times compare by **UTC ticks and offset** — two values representing
  the same instant with different offsets are *not* equal. This is stricter
  than BCL `DateTimeOffset.Equals` (instant-only) and keeps the property
  "equal values encode to identical bytes" true.
- Equality is symmetric to hashing: equal values always hash equal.

### Schema compatibility (append-only columns)

`FieldDefinition.IsDecodableFrom(writer)` — names and kinds must match, and
the reader must accept at least the nulls a writer can produce
(`reader.Nullable` unless `!writer.Nullable`).

`FeatureSchema.IsDecodableFrom(writerSchema)` — the **prefix rule**: the
writer's fields must start with exactly the reader's fields, in order, with
matching names, kinds and compatible nullability. Columns may only be
appended; reorder, rename, retype and nullability-narrowing are
incompatible. `TryIsDecodableFrom` reports the first incompatibility for
actionable diagnostics; `FeatureBatchCodec.Decode(data, targetSchema)`
projects a batch written under a wider schema to any decodable prefix target
— the schema-compatibility test vehicle.

## Canonical binary batch format (v1)

Deterministic: equal batches encode to identical bytes. All integers and
doubles are little-endian; all strings are strict UTF-8. See
`FeatureBatchCodec` for the authoritative description.

```
Header:
  byte[5]   magic "SFBAT"
  byte      format version (currently 1)

Schema:
  int32     field count
  per field:
    string  name (int32 byte length, UTF-8 bytes)
    byte    kind (AttributeKind value; never Null, always defined)
    byte    nullable (0 or 1)
    byte    hasDescription (0 or 1)
    when 1: string description

Features:
  int32     feature count
  per feature:
    string  id (int32 byte length, UTF-8 bytes)
    per field, in schema order:
      byte  isNull (0 = value follows, 1 = null; 1 only on nullable fields)
      when 0, payload by kind:
        Boolean:        byte (0 or 1)
        Int64:          int64
        Double:         double
        String:         string
        Geometry:       int32 byte length, canonical geometry bytes
                        (GeometryCodec, byte-for-byte)
        DateTimeOffset: int64 UTC ticks, int16 offset in whole minutes
        Guid:           16 bytes (Guid.TryWriteBytes order)
```

### Date-times

`DateTimeOffset` stores the UTC ticks and the offset in whole minutes (the
BCL itself only accepts whole-minute offsets). Values are validated at read:
UTC ticks and reconstructed local ticks must lie within `DateTime`'s range
and the offset within ±14 hours, so decode can never throw
`ArgumentOutOfRangeException` from a hostile buffer — it reports a format
error with the byte offset instead. Attribute equality (see above) uses the
same pair, so a round trip is exact.

### Guards

- Header: length, magic, version.
- Counts (fields, features) are non-negative and checked against the
  remaining input before allocation: each field needs at least 7 bytes, each
  feature at least `4 + field count` bytes.
- Flag bytes (nullable, hasDescription, isNull, boolean) must be 0 or 1.
- Schema names and feature ids must be non-empty, non-whitespace, strict
  UTF-8; duplicate schema names are rejected.
- Geometry payloads are length-prefixed and decoded with `GeometryCodec`;
  its errors (with byte offsets) are embedded in the batch error.
- Trailing bytes after the final feature are rejected.
- Decode errors are actionable: `FeatureBatchFormatException` (or
  `TryDecode`'s error) carries the offending byte offset.

## Guards

- Round-trip, seeded-property, malformed-input and determinism tests live
  with the types: `tests/unit/Spatial.Core.Tests` (Phase 2).
- The codec sizes the output before allocating (one buffer per encode);
  geometry attributes are encoded once for sizing and again in the write
  pass. A growable writer is a later optimisation only if profiling says so.