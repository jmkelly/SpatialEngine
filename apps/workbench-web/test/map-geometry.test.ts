import { test } from "node:test";
import assert from "node:assert/strict";
import { collectCoordinates, collectionCoordinates } from "../src/map-geometry.ts";

/**
 * Bounds flattening is pure projection math: every coordinate of every
 * geometry kind is collected, and a null geometry contributes nothing.
 */

test("collects coordinates from every simple geometry kind", () => {
  const target: [number, number][] = [];
  collectCoordinates(target, { type: "Point", coordinates: [1, 2] });
  collectCoordinates(target, { type: "MultiPoint", coordinates: [[3, 4], [5, 6]] });
  collectCoordinates(target, { type: "LineString", coordinates: [[7, 8], [9, 10]] });
  collectCoordinates(target, { type: "Polygon", coordinates: [[[11, 12], [13, 14], [11, 12]]] });
  assert.deepEqual(target, [
    [1, 2], [3, 4], [5, 6], [7, 8], [9, 10], [11, 12], [13, 14], [11, 12],
  ]);
});

test("recurses through multipolygons and geometry collections", () => {
  const target: [number, number][] = [];
  collectCoordinates(target, { type: "MultiPolygon", coordinates: [[[[1, 1], [2, 2], [1, 1]]]] });
  collectCoordinates(target, {
    type: "GeometryCollection",
    geometries: [{ type: "Point", coordinates: [3, 3] }, { type: "LineString", coordinates: [[4, 4], [5, 5]] }],
  });
  assert.deepEqual(target, [[1, 1], [2, 2], [1, 1], [3, 3], [4, 4], [5, 5]]);
});

test("collects a whole collection and ignores null geometries", () => {
  const collection: GeoJSON.FeatureCollection<GeoJSON.Geometry | null> = {
    type: "FeatureCollection",
    features: [
      { type: "Feature", properties: {}, geometry: { type: "Point", coordinates: [10, 20] } },
      { type: "Feature", properties: {}, geometry: null },
      { type: "Feature", properties: {}, geometry: { type: "Point", coordinates: [-1, -2] } },
    ],
  };
  assert.deepEqual(collectionCoordinates(collection), [[10, 20], [-1, -2]]);
});
