import { test } from "node:test";
import assert from "node:assert/strict";
import {
  addLayer,
  defaultServices,
  defaultStyle,
  fromMap,
  geometryTypesOf,
  inferGeometryKind,
  layerSpecs,
  newLayerId,
  removeLayer,
  reorderLayers,
  sourceId,
  toMap,
  toMapLayers,
  updateLayerName,
  updateLayerStyle,
  type ComposerLayer,
} from "../src/composer.ts";

/**
 * The composer model is pure: ordering, style and the mapping onto the
 * host's neutral `Map` contract are pinned here without a renderer.
 */

function layer(dataset: string, overrides: Partial<ComposerLayer> = {}): ComposerLayer {
  return {
    id: newLayerId(),
    store: "demo",
    dataset,
    name: dataset,
    geometry: "point",
    kind: "feature",
    style: defaultStyle("point"),
    featureCount: 3,
    layerId: null,
    ...overrides,
  };
}

test("infers a geometry family from the feature geometry types", () => {
  assert.equal(inferGeometryKind(["Point", "MultiPoint"]), "point");
  assert.equal(inferGeometryKind(["LineString"]), "line");
  assert.equal(inferGeometryKind(["Polygon", "MultiPolygon"]), "polygon");
  assert.equal(inferGeometryKind(["Point", "Polygon"]), "mixed");
  assert.equal(inferGeometryKind([]), "mixed");
  assert.equal(inferGeometryKind(["GeometryCollection"]), "mixed");
});

test("geometryTypesOf reports null geometries without throwing", () => {
  const collection: GeoJSON.FeatureCollection<GeoJSON.Geometry | null> = {
    type: "FeatureCollection",
    features: [
      { type: "Feature", properties: {}, geometry: { type: "Point", coordinates: [0, 0] } },
      { type: "Feature", properties: {}, geometry: null },
    ],
  };
  assert.deepEqual(geometryTypesOf(collection), ["Point", "Unknown"]);
});

test("default styles differ by geometry family and cycle the palette", () => {
  const point = defaultStyle("point", 0);
  const line = defaultStyle("line", 1);
  const polygon = defaultStyle("polygon", 2);
  assert.equal(point.color, "#4fc3f7");
  assert.notEqual(point.color, line.color);
  assert.notEqual(line.color, polygon.color);
  assert.ok(point.radius > 0);
  assert.equal(point.opacity, 0.9);
  assert.ok(polygon.opacity < 1);
});

test("add, remove, rename and restyle are pure list operations", () => {
  const a = layer("public.a");
  const b = layer("public.b");
  const withBoth = addLayer(addLayer([], a), b);
  assert.deepEqual(withBoth.map((entry) => entry.dataset), ["public.a", "public.b"]);

  const renamed = updateLayerName(withBoth, a.id, "Cities");
  assert.equal(renamed[0]!.name, "Cities");
  assert.equal(withBoth[0]!.name, "public.a", "the original list is untouched");

  const styled = updateLayerStyle(renamed, a.id, { color: "#ff0000", opacity: 0.5 });
  assert.equal(styled[0]!.style.color, "#ff0000");
  assert.equal(styled[0]!.style.opacity, 0.5);
  assert.equal(styled[0]!.style.radius, renamed[0]!.style.radius, "unspecified style fields are preserved");

  assert.deepEqual(removeLayer(withBoth, a.id).map((entry) => entry.dataset), ["public.b"]);
});

test("reorderLayers moves an item and tolerates no-op or out-of-range moves", () => {
  const layers = [layer("a"), layer("b"), layer("c")];
  assert.deepEqual(reorderLayers(layers, 2, 0).map((entry) => entry.dataset), ["c", "a", "b"]);
  assert.deepEqual(reorderLayers(layers, 0, 1).map((entry) => entry.dataset), ["b", "a", "c"]);
  assert.deepEqual(reorderLayers(layers, 1, 1).map((entry) => entry.dataset), ["a", "b", "c"]);
  assert.deepEqual(reorderLayers(layers, 0, 9).map((entry) => entry.dataset), ["a", "b", "c"]);
});

test("toMap assigns -1 to new layers and preserves list order and kinds", () => {
  const layers = [layer("public.a"), layer("public.b", { name: "B layer", kind: "image" })];
  const mapped = toMapLayers(layers);
  assert.deepEqual(mapped.map(({ style: _style, ...rest }) => rest), [
    { dataset: "public.a", layerId: -1, name: "public.a", kind: "feature" },
    { dataset: "public.b", layerId: -1, name: "B layer", kind: "image" },
  ]);
  assert.ok(mapped.every((entry) => typeof entry.style === "string" && entry.style.length > 0), "every layer carries a style fragment");

  const map = toMap({ name: "  cities  ", store: "memory", layers, services: ["feature", "map", "tiles"] });
  assert.equal(map.name, "cities", "the name is trimmed for the flat-identifier grammar");
  assert.equal(map.store, "memory");
  assert.deepEqual(map.services, ["feature", "map", "tiles"]);
  assert.equal(map.layers.length, 2);
});

test("fromMap preserves order, stable layer ids and the service set, then maps back", () => {
  const draft = fromMap({
    name: "cities",
    store: "memory",
    services: ["feature", "wfs"],
    layers: [
      { dataset: "public.b", layerId: 4, name: null },
      { dataset: "public.a", layerId: 7, name: "A", kind: "image" },
    ],
  });

  assert.equal(draft.store, "memory");
  assert.deepEqual(draft.services, ["feature", "wfs"]);
  assert.deepEqual(draft.layers.map((entry) => entry.dataset), ["public.b", "public.a"]);
  assert.deepEqual(draft.layers.map((entry) => entry.layerId), [4, 7]);
  assert.deepEqual(draft.layers.map((entry) => entry.kind), ["feature", "image"]);
  assert.equal(draft.layers[0]!.name, "public.b", "a missing name falls back to the dataset");

  assert.deepEqual(toMapLayers(draft.layers).map(({ style: _style, ...rest }) => rest), [
    { dataset: "public.b", layerId: 4, name: "public.b", kind: "feature" },
    { dataset: "public.a", layerId: 7, name: "A", kind: "image" },
  ]);
});

test("layer style survives a map round-trip (ADR-0047)", () => {
  const point = layer("public.a", {
    geometry: "point",
    style: { color: "#123456", opacity: 0.4, lineWidth: 2, radius: 11, visible: false },
  });
  const [published] = toMapLayers([point]);
  assert.ok(published?.style, "the style fragment is persisted");

  const restored = fromMap({ name: "svc", store: "memory", services: ["map"], layers: [published!] });
  assert.deepEqual(restored.layers[0]!.style, { color: "#123456", opacity: 0.4, lineWidth: 2, radius: 11, visible: false });

  const line = layer("public.b", {
    geometry: "line",
    style: { color: "#abcdef", opacity: 0.8, lineWidth: 5, radius: 5, visible: true },
  });
  const [linePublish] = toMapLayers([line]);
  const lineRestored = fromMap({ name: "svc", store: "memory", services: ["map"], layers: [linePublish!] });
  assert.equal(lineRestored.layers[0]!.style.color, "#abcdef");
  assert.equal(lineRestored.layers[0]!.style.lineWidth, 5);
});

test("a layer without a persisted style falls back to the default", () => {
  const draft = fromMap({
    name: "svc",
    store: "memory",
    services: [],
    layers: [{ dataset: "public.a", layerId: 0, name: null }],
  });
  assert.deepEqual(draft.layers[0]!.style, defaultStyle("mixed", 0));
  assert.deepEqual(draft.layers[0]!.kind, "feature", "an absent kind defaults to feature");
  assert.deepEqual(draft.services, [], "an empty service set is a valid draft");
});

test("a new draft starts with the feature service enabled", () => {
  assert.deepEqual(defaultServices, ["feature"]);
});

test("layerSpecs emit one shape per geometry family with the layer's paint", () => {
  const point = layer("p", { geometry: "point", style: { ...defaultStyle("point"), color: "#112233", radius: 9 } });
  const pointSpecs = layerSpecs(point, sourceId(point.id));
  assert.deepEqual(pointSpecs.map((spec) => spec.type), ["circle"]);
  assert.equal((pointSpecs[0] as { paint: Record<string, unknown> }).paint["circle-radius"], 9);
  assert.equal((pointSpecs[0] as { paint: Record<string, unknown> }).paint["circle-color"], "#112233");
  assert.equal((pointSpecs[0] as { source: string }).source, sourceId(point.id));

  assert.deepEqual(layerSpecs(layer("l", { geometry: "line" }), "s").map((spec) => spec.type), ["line"]);
  assert.deepEqual(layerSpecs(layer("f", { geometry: "polygon" }), "s").map((spec) => spec.type), ["fill", "line"]);
  assert.deepEqual(layerSpecs(layer("m", { geometry: "mixed" }), "s").map((spec) => spec.type), ["fill", "line", "circle"]);
});

test("layerSpecs carry visibility into layout", () => {
  const hidden = layer("p", { style: { ...defaultStyle("point"), visible: false } });
  const specs = layerSpecs(hidden, "s");
  assert.deepEqual((specs[0] as { layout: { visibility: string } }).layout.visibility, "none");
});
