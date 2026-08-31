import type { Feature, FeatureBatch } from "@spatial/client";

/**
 * The browser's MapLibre geometry adapter (Phase 10): decodes canonical
 * SGEOM interchange bytes (ADR-0020) into GeoJSON geometry so the map can
 * render feature geometry attributes that cross the wire as raw canonical
 * bytes. The mirror of the .NET GeometryCodec layout — header, recursive
 * node, little-endian doubles — with bounds-checked reads and byte-accurate
 * errors, so malformed wire data degrades honestly instead of corrupting the
 * map. Size-capped so a hostile stream cannot exhaust the browser.
 */

/** The GeoJSON geometry shapes the renderer understands. */
export type DecodedGeometry =
  | { type: "Point"; coordinates: number[] }
  | { type: "MultiPoint"; coordinates: number[][] }
  | { type: "LineString"; coordinates: number[][] }
  | { type: "MultiLineString"; coordinates: number[][][] }
  | { type: "Polygon"; coordinates: number[][][] }
  | { type: "MultiPolygon"; coordinates: number[][][][] }
  | { type: "GeometryCollection"; geometries: DecodedGeometry[] };

/** The types of the canonical node format. */
const GeometryType = {
  Point: 1,
  LineString: 2,
  Polygon: 3,
  MultiPoint: 4,
  MultiLineString: 5,
  MultiPolygon: 6,
  GeometryCollection: 7,
} as const;

/** The coordinate layouts and their ordinate counts. */
const LayoutOrdinates: Record<number, number> = { 0: 2, 1: 3, 2: 3, 3: 4 };

/** The fixed SGEOM header: 5 magic bytes + format version. */
const HeaderLength = 6;

/** Maximum nesting depth and total elements accepted from one payload. */
const MaxNestingDepth = 256;
const MaxElements = 100_000;

/** Thrown for malformed canonical geometry; message names the offending byte. */
export class SgeomFormatError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "SgeomFormatError";
  }
}

/** Decodes one canonical SGEOM payload into GeoJSON, or throws SgeomFormatError. */
export function decodeSgeom(bytes: Uint8Array, budget = MaxElements): DecodedGeometry {
  const reader = new Reader(bytes, budget);
  if (reader.takeAscii(HeaderLength - 1) !== "SGEOM") {
    throw new SgeomFormatError("not a canonical geometry: magic bytes are not 'SGEOM'");
  }
  const version = reader.u8();
  if (version !== 1) {
    throw new SgeomFormatError(`unsupported canonical geometry format version ${version}`);
  }
  return reader.node(0);
}

/** Converts a decoded geometry to a MapLibre-ready GeoJSON feature. */
export function toGeoJsonFeature(decoded: DecodedGeometry, id: string, properties: Record<string, unknown>): GeoJSON.Feature {
  // MapLibre's GeoJSON worker does not reliably preserve string feature ids
  // through its vector-tile pipeline (queryRenderedFeatures returns the
  // numeric index), so the wire id travels in a property as __fid as well.
  return { type: "Feature", id, properties: { ...properties, __fid: id }, geometry: decoded as GeoJSON.Geometry };
}

/** The renderable geometry of one batch feature, or null when it carries none. */
export function featureGeometry(feature: Feature): DecodedGeometry | null {
  for (let i = 0; i < feature.attributes.length; i++) {
    const attribute = feature.attributes[i];
    if (attribute?.kind === "Geometry") {
      return decodeSgeom(attribute.value);
    }
  }
  return null;
}

/** Maps a decoded feature batch to a GeoJSON feature collection (renderable rows). */
export function batchToGeoJson(batch: FeatureBatch): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  const fields = batch.schema.fields;
  for (const feature of batch.features) {
    const geometry = featureGeometry(feature);
    if (geometry === null) continue;
    features.push(toGeoJsonFeature(geometry, feature.id, attributeProperties(feature, fields)));
  }
  return { type: "FeatureCollection", features };
}

/** Feature attributes as a plain properties map (for selection inspection). */
export function attributeProperties(feature: Feature, fields: { name: string }[]): Record<string, unknown> {
  const properties: Record<string, unknown> = {};
  feature.attributes.forEach((attribute, index) => {
    const name = fields[index]?.name ?? `field${index}`;
    properties[name] = attributeToJson(attribute);
  });
  return properties;
}

function attributeToJson(attribute: Feature["attributes"][number]): unknown {
  if (attribute === undefined) return undefined;
  switch (attribute.kind) {
    case "Null":
      return null;
    case "Boolean":
      return attribute.value;
    case "Int64":
      return attribute.value.toString();
    case "Double":
      return attribute.value;
    case "String":
      return attribute.value;
    case "Geometry":
      return "(geometry)";
    case "DateTimeOffset":
      return attribute.value.utcTicks.toString();
    case "Guid":
      return attribute.value;
  }
}

/** A bounds-checked little-endian reader over canonical geometry bytes. */
class Reader {
  private readonly bytes: Uint8Array;
  private readonly view: DataView;
  private readonly budget: number;
  cursor = 0;
  private elements = 0;

  constructor(bytes: Uint8Array, budget: number) {
    this.bytes = bytes;
    this.budget = budget;
    this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }

  u8(): number {
    this.require(1);
    return this.bytes[this.cursor++]!;
  }

  i32(): number {
    this.require(4);
    const value = this.view.getInt32(this.cursor, true);
    this.cursor += 4;
    return value;
  }

  f64(): number {
    this.require(8);
    const value = this.view.getFloat64(this.cursor, true);
    this.cursor += 8;
    return value;
  }

  takeAscii(count: number): string {
    this.require(count);
    const text = String.fromCharCode(...this.bytes.subarray(this.cursor, this.cursor + count));
    this.cursor += count;
    return text;
  }

  private require(count: number): void {
    if (count < 0 || this.cursor + count > this.bytes.length) {
      throw new SgeomFormatError(
        `canonical geometry is truncated: need ${count} byte(s) at offset ${this.cursor}, but only ${this.bytes.length - this.cursor} remain`,
      );
    }
  }

  private charge(): void {
    if (++this.elements > this.budget) {
      throw new SgeomFormatError(`canonical geometry exceeds the ${this.budget}-element decode budget`);
    }
  }

  /** Reads one recursive geometry node. */
  node(depth: number): DecodedGeometry {
    if (depth > MaxNestingDepth) {
      throw new SgeomFormatError(`canonical geometry nests deeper than ${MaxNestingDepth} levels`);
    }
    this.charge();
    const layoutByte = this.u8();
    const stride = LayoutOrdinates[layoutByte];
    if (stride === undefined) {
      throw new SgeomFormatError(`unknown coordinate layout ${layoutByte} at byte ${this.cursor - 1}`);
    }
    const type = this.u8();
    this.skipCrs();
    switch (type) {
      case GeometryType.Point:
        return this.point(stride);
      case GeometryType.LineString:
        return { type: "LineString", coordinates: this.line(stride) };
      case GeometryType.Polygon: {
        const ringCount = this.count();
        const coordinates: number[][][] = [];
        for (let i = 0; i < ringCount; i++) {
          const ring = this.node(depth + 1);
          if (ring.type !== "LineString") {
            throw new SgeomFormatError(`ring ${i} is a ${ring.type}, expected a line string`);
          }
          coordinates.push(ring.coordinates);
        }
        return { type: "Polygon", coordinates };
      }
      case GeometryType.MultiPoint: {
        const count = this.count();
        const coordinates: number[][] = [];
        for (let i = 0; i < count; i++) {
          const child = this.node(depth + 1);
          if (child.type !== "Point") {
            throw new SgeomFormatError(`multi-point element ${i} is a ${child.type}, expected a point`);
          }
          coordinates.push(child.coordinates);
        }
        return { type: "MultiPoint", coordinates };
      }
      case GeometryType.MultiLineString: {
        const count = this.count();
        const coordinates: number[][][] = [];
        for (let i = 0; i < count; i++) {
          const child = this.node(depth + 1);
          if (child.type !== "LineString") {
            throw new SgeomFormatError(`multi-line element ${i} is a ${child.type}, expected a line string`);
          }
          coordinates.push(child.coordinates);
        }
        return { type: "MultiLineString", coordinates };
      }
      case GeometryType.MultiPolygon: {
        const count = this.count();
        const coordinates: number[][][][] = [];
        for (let i = 0; i < count; i++) {
          const child = this.node(depth + 1);
          if (child.type !== "Polygon") {
            throw new SgeomFormatError(`multi-polygon element ${i} is a ${child.type}, expected a polygon`);
          }
          coordinates.push(child.coordinates);
        }
        return { type: "MultiPolygon", coordinates };
      }
      case GeometryType.GeometryCollection: {
        const count = this.count();
        const geometries: DecodedGeometry[] = [];
        for (let i = 0; i < count; i++) geometries.push(this.node(depth + 1));
        return { type: "GeometryCollection", geometries };
      }
      default:
        throw new SgeomFormatError(`unknown geometry type ${type} at byte ${this.cursor - 1}`);
    }
  }

  private point(stride: number): { type: "Point"; coordinates: number[] } {
    const hasCoordinate = this.u8() === 1;
    return { type: "Point", coordinates: hasCoordinate ? this.coordinate(stride) : [] };
  }

  private line(stride: number): number[][] {
    const count = this.count();
    const coordinates: number[][] = [];
    for (let i = 0; i < count; i++) coordinates.push(this.coordinate(stride));
    return coordinates;
  }

  private coordinate(stride: number): number[] {
    const coordinates: number[] = [];
    for (let i = 0; i < stride; i++) coordinates.push(this.f64());
    return coordinates;
  }

  private count(): number {
    const count = this.i32();
    if (count < 0) {
      throw new SgeomFormatError(`negative element count ${count} at byte ${this.cursor - 4}`);
    }
    this.charge();
    return count;
  }

  private skipCrs(): void {
    const hasCrs = this.u8() === 1;
    if (!hasCrs) return;
    const authorityLength = this.i32();
    this.require(authorityLength);
    this.cursor += authorityLength;
    const codeLength = this.i32();
    this.require(codeLength);
    this.cursor += codeLength;
  }
}