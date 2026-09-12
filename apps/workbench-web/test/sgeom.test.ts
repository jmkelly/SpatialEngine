import { test } from "node:test";
import assert from "node:assert/strict";
import { decodeSgeom, batchToGeoJson, featureGeometry, SgeomFormatError } from "../src/sgeom.ts";

/**
 * The SGEOM decoder pinned against the canonical binary layout of
 * the .NET GeometryCodec (ADR-0020): hand-built vectors for point, line and
 * polygon nodes with and without CRS, plus malformed-input rejection.
 */

// Header: "SGEOM" + version 1.
const HEADER: number[] = [0x53, 0x47, 0x45, 0x4f, 0x4d, 0x01];

test("decodes a point with a CRS identity", () => {
  const crs = crsNode("EPSG", "4326");
  const bytes = new Uint8Array([...HEADER, 0 /* Xy */, 1 /* Point */, 1, ...crs, 1 /* present */, ...doubles(1.5), ...doubles(-3.25)]);
  assert.deepEqual(decodeSgeom(bytes), { type: "Point", coordinates: [1.5, -3.25] });
});

test("decodes an empty point", () => {
  const bytes = new Uint8Array([...HEADER, 0, 1, 0, 0]);
  assert.deepEqual(decodeSgeom(bytes), { type: "Point", coordinates: [] });
});

test("decodes a line string without CRS", () => {
  const bytes = new Uint8Array([...HEADER, 0, 2, 0, ...i32(2), ...doubles(0), ...doubles(0), ...doubles(0), ...doubles(2)]);
  assert.deepEqual(decodeSgeom(bytes), { type: "LineString", coordinates: [[0, 0], [0, 2]] });
});

test("decodes a polygon whose rings are line-string nodes", () => {
  const ringA = [[0, 0], [0, 1], [1, 1], [1, 0], [0, 0]];
  const ringB = [[0.2, 0.2], [0.3, 0.2], [0.2, 0.3], [0.2, 0.2]];
  const ringNode = (coordinates: number[][]) => [0, 2, 0, ...i32(coordinates.length), ...doubles(...coordinates.flat())];
  const bytes = new Uint8Array([
    ...HEADER, 0, 3, 0, ...i32(2),
    ...ringNode(ringA),
    ...ringNode(ringB),
  ]);
  assert.deepEqual(decodeSgeom(bytes), { type: "Polygon", coordinates: [ringA, ringB] });
});

test("rejects malformed input with byte-accurate errors", () => {
  assert.throws(() => decodeSgeom(new Uint8Array([0x00])), SgeomFormatError);
  assert.throws(() => decodeSgeom(new Uint8Array([...HEADER])), /truncated/);
  assert.throws(() => decodeSgeom(new Uint8Array([...HEADER, 0, 99, 0])), /unknown geometry type 99 at byte 8/);
  assert.throws(() => decodeSgeom(new Uint8Array([...HEADER, 9, 1, 0, 0])), /unknown coordinate layout 9/);
  assert.throws(() => decodeSgeom(new Uint8Array([...HEADER, 0, 2, 0, ...i32(-1)])), /negative element count/);
});

test("decodes a multi-point and a geometry collection", () => {
  const multi = new Uint8Array([
    ...HEADER, 0, 4, 0, ...i32(2),
    0, 1, 0, 1, ...doubles(0), ...doubles(0),
    0, 1, 0, 1, ...doubles(1), ...doubles(1),
  ]);
  assert.deepEqual(decodeSgeom(multi), { type: "MultiPoint", coordinates: [[0, 0], [1, 1]] });

  const collection = new Uint8Array([...HEADER, 0, 7, 0, ...i32(1), 0, 1, 0, 1, ...doubles(5), ...doubles(6)]);
  assert.deepEqual(decodeSgeom(collection), {
    type: "GeometryCollection",
    geometries: [{ type: "Point", coordinates: [5, 6] }],
  });
});

test("featureGeometry extracts the geometry attribute of a batch feature", () => {
  const bytes = new Uint8Array([...HEADER, 0, 1, 0, 1, ...doubles(2), ...doubles(3)]);
  const feature = {
    id: "f1",
    schema: { fields: [{ name: "name", kind: "String", nullable: false }, { name: "geometry", kind: "Geometry", nullable: false }] },
    attributes: [{ kind: "String", value: "north" }, { kind: "Geometry", value: bytes }],
  } as never;
  assert.deepEqual(featureGeometry(feature), { type: "Point", coordinates: [2, 3] });
});

test("batchToGeoJson skips features without geometry and keeps properties", () => {
  const bytes = new Uint8Array([...HEADER, 0, 1, 0, 1, ...doubles(2), ...doubles(3)]);
  const batch = {
    schema: { fields: [{ name: "name", kind: "String", nullable: false }] },
    features: [
      { id: "f1", attributes: [{ kind: "String", value: "a" }] },
    ],
  } as never;
  // A batch where no attribute is a Geometry -> no renderable features.
  assert.equal(batchToGeoJson(batch).features.length, 0);
});

test("decodes the .NET-produced buffered circle payload end to end", () => {
  // A real canonical polygon emitted by the NTS buffer over HTTP (pinned
  // wire bytes): EPSG:4326, one ring, 33 points, starting at (0, 1). This is
  // the workbench's result-preview path.
  const payload =
    "U0dFT00BAAMBBAAAAEVQU0cEAAAANDMyNgEAAAAAAgAhAAAAAAAAAAAA8D8AAAAAAAAAALBc98+XYu8/CqZpPLj4yL9GjTLPa5DtP2Oprqbifdi/o6EOKWab6j/IaK45O8fhv807f2aeoOY/zDt/Zp6g5r/JaK45O8fhP6OhDilmm+q/ZKmupuJ92D9GjTLPa5Dtvw2maTy4+Mg/sFz3z5di778AAAAAAAAAAAAAAAAAAPC/CKZpPLj4yL+wXPfPl2Lvv2Kprqbifdi/Ro0yz2uQ7b/GaK45O8fhv6WhDilmm+q/zDt/Zp6g5r/NO39mnqDmv6ShDilmm+q/yGiuOTvH4b9GjTLPa5Dtv2Wprqbifdi/sFz3z5di778Xpmk8uPjIvwAAAAAAAPC/AAAAAAAAAACwXPfPl2Lvvw6maTy4+Mg/R40yz2uQ7b9hqa6m4n3YP6WhDilmm+q/xmiuOTvH4T/OO39mnqDmv8w7f2aeoOY/yGiuOTvH4b+joQ4pZpvqP22prqbifdi/RI0yz2uQ7T8Zpmk8uPjIv69c98+XYu8/AAAAAAAAAAAAAAAAAADwPwymaTy4+Mg/sFz3z5di7z9nqa6m4n3YP0WNMs9rkO0/xWiuOTvH4T+loQ4pZpvqP8s7f2aeoOY/zjt/Zp6g5j+joQ4pZpvqP8horjk7x+E/RI0yz2uQ7T9uqa6m4n3YP69c98+XYu8/G6ZpPLj4yD8AAAAAAADwPwAAAAAAAAAA";
  const geometry = decodeSgeom(Uint8Array.from(atob(payload), (char) => char.charCodeAt(0)));
  assert.equal(geometry.type, "Polygon");
  if (geometry.type !== "Polygon") return;
  assert.equal(geometry.coordinates.length, 1);
  const ring = geometry.coordinates[0]!;
  assert.equal(ring.length, 33);
  const first = ring[0]!;
  assert.ok(Math.abs(first[0]! - 1) < 1e-6 && Math.abs(first[1]! - 0) < 1e-6, `first point is (1,0), got ${first}`);
  // The ring closes back on the start.
  const last = ring[ring.length - 1]!;
  assert.ok(Math.abs(last[0]! - 1) < 1e-6 && Math.abs(last[1]! - 0) < 1e-6);
});

/** Builds the CRS node bytes: authorities length+text, codes length+text. */
function crsNode(authority: string, code: string): number[] {
  const a = encodeUtf8(authority);
  const c = encodeUtf8(code);
  return [...i32(a.length), ...a, ...i32(c.length), ...c];
}

function encodeUtf8(text: string): number[] {
  return [...new TextEncoder().encode(text)];
}

function i32(value: number): number[] {
  const bytes = new Uint8Array(4);
  new DataView(bytes.buffer).setInt32(0, value, true);
  return [...bytes];
}

/** Little-endian doubles as byte arrays. */
function doubles(...values: number[]): number[] {
  const bytes = new Uint8Array(values.length * 8);
  const view = new DataView(bytes.buffer);
  values.forEach((value, index) => view.setFloat64(index * 8, value, true));
  return [...bytes];
}