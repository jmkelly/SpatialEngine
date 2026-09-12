import { test } from "node:test";
import assert from "node:assert/strict";
import {
  getFeature,
  getLayer,
  getService,
  queryFeatures,
} from "@esri/arcgis-rest-feature-service";
import type { IQueryFeaturesResponse } from "@esri/arcgis-rest-feature-service";
import { request } from "@esri/arcgis-rest-request";

/** `queryFeatures` is typed as a union; the JSON responses under test are always feature sets. */
const featureSet = (result: unknown): IQueryFeaturesResponse => result as IQueryFeaturesResponse;

/**
 * Real-client proof for the GeoServices boundary adapter (ADR-0035): drives
 * the live Spatial.Host with the *official* Esri client libraries, not the
 * engine's own SDK. ArcGIS REST JS is what ArcGIS Maps SDK for JS and the
 * rest of the Esri ecosystem use under the hood, so a green run is evidence
 * an unmodified Esri client can discover and query the facade.
 *
 * Skipped unless SPATIAL_HOST_URL points at a running host; `eng/e2e-web.sh`
 * starts one and exports it.
 */
const HOST = process.env.SPATIAL_HOST_URL;
const ENABLED = HOST !== undefined && HOST.length > 0;
const skip = ENABLED ? false : "set SPATIAL_HOST_URL to a running host";

const ROOT = `${HOST}/arcgis/rest/services`;
const SERVICE = `${ROOT}/demo/FeatureServer`;
const LAYER = `${SERVICE}/0`;

test("an unmodified Esri client discovers the FeatureServer and layer", { skip }, async () => {
  const service = await getService({ url: SERVICE });
  const names = service.layers.map((layer) => layer.name);
  assert.ok(names.includes("cities"), `expected a 'cities' layer, got ${names.join(", ")}`);

  const layer = await getLayer({ url: LAYER });
  assert.equal(layer.objectIdField, "OBJECTID");
  assert.equal(layer.geometryType, "esriGeometryPoint");
  assert.ok(layer.fields?.some((field) => field.name === "name"), "the layer schema advertises 'name'");
  assert.ok(layer.fields?.some((field) => field.name === "population"), "the layer schema advertises 'population'");
});

test("an unmodified Esri client queries attributes and geometry", { skip }, async () => {
  const result = featureSet(await queryFeatures({
    url: LAYER,
    where: "name = 'Berlin'",
    outFields: ["name", "population"],
    returnGeometry: true,
  }));

  assert.equal(result.features.length, 1);
  const feature = result.features[0]!;
  assert.equal(feature.attributes.name, "Berlin");
  assert.ok(typeof feature.attributes.OBJECTID === "number", "the response carries the OBJECTID");
  assert.ok(feature.geometry, "returnGeometry=true carries a geometry");
});

test("an unmodified Esri client reads one feature by its object id", { skip }, async () => {
  const page = await queryFeatures({ url: LAYER, outFields: ["name"], returnIdsOnly: true } as never);
  const ids = (page as unknown as { objectIds: number[] }).objectIds;
  assert.ok(ids.length > 0);

  const feature = await getFeature({ url: LAYER, id: ids[0]! });
  assert.equal(Number(feature.attributes?.OBJECTID), ids[0]);
});

test("an unmodified Esri client pages and counts the matched set", { skip }, async () => {
  const count = (await queryFeatures({ url: LAYER, returnCountOnly: true })) as unknown as { count: number };
  assert.ok(count.count >= 2);

  const first = featureSet(await queryFeatures({
    url: LAYER,
    where: "1=1",
    orderByFields: "name ASC",
    resultRecordCount: 1,
  }));
  const second = featureSet(await queryFeatures({
    url: LAYER,
    where: "1=1",
    orderByFields: "name ASC",
    resultRecordCount: 1,
    resultOffset: 1,
  }));

  assert.equal(first.features.length, 1);
  assert.equal(second.features.length, 1);
  assert.notEqual(first.features[0]!.attributes.OBJECTID, second.features[0]!.attributes.OBJECTID);
  assert.ok(first.exceededTransferLimit, "a partial page reports exceededTransferLimit");
});

test("an unmodified Esri client runs a Geometry Service operation", { skip }, async () => {
  const result = await request(`${ROOT}/Geometry/GeometryServer/project`, {
    params: {
      geometries: JSON.stringify([{ x: 13.405, y: 52.52, spatialReference: { wkid: 4326 } }]),
      inSR: JSON.stringify({ wkid: 4326 }),
      outSR: JSON.stringify({ wkid: 32632 }),
      f: "json",
    },
  });

  const geometry = result.geometries[0];
  assert.equal(geometry.spatialReference.wkid, 32632);
  assert.ok(geometry.x > 100_000, "Berlin projects into UTM 32N easting");
});
