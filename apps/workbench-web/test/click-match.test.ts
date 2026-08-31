import { test } from "node:test";
import assert from "node:assert/strict";
import { lngSpanPixels, mercatorY, nearestFeatureId, representativeCoordinate } from "../src/click-match.ts";

/**
 * Coordinate-based click selection (Phase 10): matching is pure projection
 * math, so the unit tests pin the grid geometry without a renderer.
 */

const point = (id: string, lng: number, lat: number): GeoJSON.Feature => ({
  type: "Feature",
  id,
  properties: {},
  geometry: { type: "Point", coordinates: [lng, lat] },
});

test("matches the nearest point within the click tolerance", () => {
  const features = [point("a", -1, 0), point("b", 0, 0), point("c", 1, 2)];
  // At zoom ~5.7, 16px spans ~0.6 degrees of lng: a click at (0.1, 0) picks b.
  const span = lngSpanPixels(5.76, 16);
  assert.equal(nearestFeatureId(features, { lng: 0.1, lat: 0.05 }, span), "b");
  // A click far from every feature clears the selection.
  assert.equal(nearestFeatureId(features, { lng: 10, lat: 10 }, span), null);
});

test("the tolerance scales with zoom (pixels are screen-space)", () => {
  assert.ok(lngSpanPixels(10, 16) < lngSpanPixels(2, 16));
});

test("polygon and line geometries match at their centroid", () => {
  const polygon: GeoJSON.Feature = {
    type: "Feature",
    id: "poly",
    properties: {},
    geometry: { type: "Polygon", coordinates: [[[0, 0], [2, 0], [2, 2], [0, 2], [0, 0]]] },
  };
  const line: GeoJSON.Feature = {
    type: "Feature",
    id: "line",
    properties: {},
    geometry: { type: "LineString", coordinates: [[10, 10], [12, 10]] },
  };
  // The representative coordinate is the vertex average (not the true
  // centroid); good enough for click matching within the tolerance.
  assert.deepEqual(representativeCoordinate(polygon.geometry), [0.8, 0.8]);
  assert.deepEqual(representativeCoordinate(line.geometry), [11, 10]);
  const span = lngSpanPixels(4, 16);
  assert.equal(nearestFeatureId([polygon, line], { lng: 1.1, lat: 1.1 }, span), "poly");
  assert.equal(nearestFeatureId([polygon, line], { lng: 11.1, lat: 10.1 }, span), "line");
});

test("mercator y is monotonic across latitudes (south maps above north)", () => {
  assert.ok(mercatorY(10) < mercatorY(0));
  assert.ok(mercatorY(0) < mercatorY(-10));
  assert.ok(Math.abs(mercatorY(0) - 0.5) < 1e-9);
});