/**
 * The canonical feature-batch binary interchange (ADR-0020, format "SFBAT"
 * v1) decoded in TypeScript: the byte-for-byte mirror of the .NET
 * FeatureBatchCodec so browser clients can read scan/query streams. Geometry
 * attributes stay as raw canonical bytes (SGEOM) — the map's renderers
 * decode them. Every count and length is bounds-checked before allocation;
 * malformed input throws FeatureBatchFormatError with a byte-accurate
 * message.
 */
export type AttributeKindName =
  | "Null"
  | "Boolean"
  | "Int64"
  | "Double"
  | "String"
  | "Geometry"
  | "DateTimeOffset"
  | "Guid";

export interface FieldDefinition {
  name: string;
  kind: AttributeKindName;
  nullable: boolean;
  description?: string;
}

export interface FeatureSchema {
  fields: FieldDefinition[];
}

export type AttributeValue =
  | { kind: "Boolean"; value: boolean }
  | { kind: "Int64"; value: bigint }
  | { kind: "Double"; value: number }
  | { kind: "String"; value: string }
  | { kind: "Geometry"; value: Uint8Array }
  | { kind: "DateTimeOffset"; value: { utcTicks: bigint; offsetMinutes: number } }
  | { kind: "Guid"; value: string }
  | { kind: "Null" };

export interface Feature {
  id: string;
  attributes: AttributeValue[];
}

export interface FeatureBatch {
  schema: FeatureSchema;
  features: Feature[];
}

export class FeatureBatchFormatError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "FeatureBatchFormatError";
  }
}

const KIND_NAMES = [
  "Null",
  "Boolean",
  "Int64",
  "Double",
  "String",
  "Geometry",
  "DateTimeOffset",
  "Guid",
] as const;

const textDecoder = new TextDecoder();

/** Decodes one SFBAT v1 buffer into a typed feature batch. */
export function decodeFeatureBatch(bytes: Uint8Array): FeatureBatch {
  const reader = new Reader(bytes);
  if (reader.takeAscii(5) !== "SFBAT") {
    throw new FeatureBatchFormatError("not a canonical feature batch: magic bytes are not 'SFBAT'");
  }
  const version = reader.u8();
  if (version !== 1) {
    throw new FeatureBatchFormatError(`unsupported canonical batch format version ${version}`);
  }

  const fields: FieldDefinition[] = [];
  const fieldCount = reader.i32();
  for (let i = 0; i < fieldCount; i++) {
    const name = reader.string();
    const kindByte = reader.u8();
    const kind = KIND_NAMES[kindByte];
    if (kind === undefined) {
      throw new FeatureBatchFormatError(`field '${name}' carries an unknown attribute kind ${kindByte} at byte ${reader.cursor - 1}`);
    }
    const nullable = reader.u8() === 1;
    const hasDescription = reader.u8() === 1;
    const description = hasDescription ? reader.string() : undefined;
    fields.push({ name, kind, nullable, ...(description !== undefined ? { description } : {}) });
  }

  const schema: FeatureSchema = { fields };
  const features: Feature[] = [];
  const featureCount = reader.i32();
  for (let f = 0; f < featureCount; f++) {
    const id = reader.string();
    const attributes: AttributeValue[] = [];
    for (const field of fields) {
      const isNull = reader.u8() === 1;
      if (isNull) {
        attributes.push({ kind: "Null" });
        continue;
      }
      attributes.push(readPayload(reader, field.kind));
    }
    features.push({ id, attributes });
  }

  return { schema, features };
}

function readPayload(reader: Reader, kind: AttributeKindName): AttributeValue {
  switch (kind) {
    case "Boolean":
      return { kind, value: reader.u8() === 1 };
    case "Int64":
      return { kind, value: reader.i64() };
    case "Double":
      return { kind, value: reader.f64() };
    case "String":
      return { kind, value: reader.string() };
    case "Geometry": {
      const length = reader.i32();
      return { kind, value: reader.take(length) };
    }
    case "DateTimeOffset": {
      const utcTicks = reader.i64();
      const offsetMinutes = reader.i16();
      return { kind, value: { utcTicks, offsetMinutes } };
    }
    case "Guid":
      return { kind, value: toGuid(reader.take(16)) };
    case "Null":
      return { kind };
  }
}

/** A bounds-checked little-endian reader over the batch bytes. */
class Reader {
  private readonly bytes: Uint8Array;
  cursor = 0;

  constructor(bytes: Uint8Array) {
    this.bytes = bytes;
  }

  u8(): number {
    this.require(1);
    return this.bytes[this.cursor++]!;
  }

  i16(): number {
    this.require(2);
    const view = this.view();
    const value = view.getInt16(this.cursor, true);
    this.cursor += 2;
    return value;
  }

  i32(): number {
    this.require(4);
    const view = this.view();
    const value = view.getInt32(this.cursor, true);
    this.cursor += 4;
    return value;
  }

  i64(): bigint {
    this.require(8);
    const view = this.view();
    const value = view.getBigInt64(this.cursor, true);
    this.cursor += 8;
    return value;
  }

  f64(): number {
    this.require(8);
    const view = this.view();
    const value = view.getFloat64(this.cursor, true);
    this.cursor += 8;
    return value;
  }

  string(): string {
    const length = this.i32();
    if (length < 0) {
      throw new FeatureBatchFormatError(`negative string length ${length} at byte ${this.cursor - 4}`);
    }
    return textDecoder.decode(this.take(length));
  }

  take(count: number): Uint8Array {
    this.require(count);
    const slice = this.bytes.subarray(this.cursor, this.cursor + count);
    this.cursor += count;
    return slice;
  }

  takeAscii(count: number): string {
    return String.fromCharCode(...this.take(count));
  }

  private view(): DataView {
    return new DataView(this.bytes.buffer, this.bytes.byteOffset, this.bytes.byteLength);
  }

  private require(count: number): void {
    if (count < 0 || this.cursor + count > this.bytes.length) {
      throw new FeatureBatchFormatError(
        `canonical batch is truncated: need ${count} byte(s) at offset ${this.cursor}, but only ${this.bytes.length - this.cursor} remain`,
      );
    }
  }
}

/**
 * Formats 16 bytes as the canonical GUID, applying .NET's mixed-endian binary
 * order (Guid.ToByteArray: the first three groups little-endian, the last big-
 * endian) so decode matches FeatureBatchCodec byte-for-byte.
 */
function toGuid(bytes: Uint8Array): string {
  const hex = (slice: Uint8Array) => [...slice].map((b) => b.toString(16).padStart(2, "0")).join("");
  const reverseHex = (slice: Uint8Array) => hex(Uint8Array.from([...slice].reverse()));
  return `${reverseHex(bytes.subarray(0, 4))}-${reverseHex(bytes.subarray(4, 6))}-${reverseHex(bytes.subarray(6, 8))}-${hex(bytes.subarray(8, 10))}-${hex(bytes.subarray(10, 16))}`;
}