import { test } from "node:test";
import assert from "node:assert/strict";
import {
  EsriCensusMapRoot,
  EsriDamageFeatureRoot,
  EsriGeometryRoot,
  EsriImageRoot,
  GeometryOperations,
  GeometrySamples,
  ParityDefaults,
  buildFeatureCountUrl,
  buildFeatureQueryUrl,
  buildGeometryUrl,
  buildImageExportUrl,
  buildMapExportUrl,
  diffIsClean,
  diffJsonLines,
  formatBbox,
  localGeometryRoot,
  localServiceRoots,
  parseBbox,
  parseSize,
  prettyJson,
} from "../src/parity.ts";

test("parseBbox accepts comma-separated bounds", () => {
  assert.deepEqual(parseBbox("-125,25,-65,50"), { minX: -125, minY: 25, maxX: -65, maxY: 50 });
});

test("parseBbox accepts whitespace separators and trims", () => {
  assert.deepEqual(parseBbox("  -125 25 -65 50 "), { minX: -125, minY: 25, maxX: -65, maxY: 50 });
});

test("parseBbox rejects wrong arity, non-numbers and inverted bounds", () => {
  assert.equal(parseBbox("-125,25,-65"), null);
  assert.equal(parseBbox("-125,25,-65,50,0"), null);
  assert.equal(parseBbox("-125,south,-65,50"), null);
  assert.equal(parseBbox(""), null);
  assert.equal(parseBbox("-65,25,-125,50"), null);
  assert.equal(parseBbox("-125,50,-65,25"), null);
});

test("parseSize accepts width,height and rejects the rest", () => {
  assert.deepEqual(parseSize("800,600"), { width: 800, height: 600 });
  assert.equal(parseSize("800"), null);
  assert.equal(parseSize("0,600"), null);
  assert.equal(parseSize("800.5,600"), null);
  assert.equal(parseSize("wide,tall"), null);
});

test("the default bbox sits inside the Census MapServer full extent (-179.6,17.9,-65.2,71.4)", () => {
  const bbox = parseBbox(ParityDefaults.bbox);
  assert.notEqual(bbox, null);
  assert.ok(bbox!.minX >= -179.6 && bbox!.maxX <= -65.2 && bbox!.minY >= 17.9 && bbox!.maxY <= 71.4);
});

test("buildMapExportUrl carries the same bbox, sr and size for f=image", () => {
  const url = new URL(buildMapExportUrl(EsriCensusMapRoot, parseBbox("-125,25,-65,50")!, 4326, { width: 800, height: 600 }));
  assert.equal(`${url.origin}${url.pathname}`, `${EsriCensusMapRoot}/export`);
  assert.equal(url.searchParams.get("bbox"), "-125,25,-65,50");
  assert.equal(url.searchParams.get("bboxSR"), "4326");
  assert.equal(url.searchParams.get("imageSR"), "4326");
  assert.equal(url.searchParams.get("size"), "800,600");
  assert.equal(url.searchParams.get("format"), "png");
  assert.equal(url.searchParams.get("f"), "image");
});

test("buildFeatureQueryUrl filters by envelope and caps the page without geometry", () => {
  const url = new URL(
    buildFeatureQueryUrl(EsriDamageFeatureRoot, 0, { where: "1=1", bbox: parseBbox("-125,25,-65,50")!, inSr: 4326, maxRecords: 20 }),
  );
  assert.equal(`${url.origin}${url.pathname}`, `${EsriDamageFeatureRoot}/0/query`);
  assert.equal(url.searchParams.get("where"), "1=1");
  assert.equal(url.searchParams.get("geometry"), "-125,25,-65,50");
  assert.equal(url.searchParams.get("geometryType"), "esriGeometryEnvelope");
  assert.equal(url.searchParams.get("spatialRel"), "esriSpatialRelIntersects");
  assert.equal(url.searchParams.get("inSR"), "4326");
  assert.equal(url.searchParams.get("outFields"), "*");
  assert.equal(url.searchParams.get("returnGeometry"), "false");
  assert.equal(url.searchParams.get("resultRecordCount"), "20");
  assert.equal(url.searchParams.get("f"), "json");
});

test("buildFeatureQueryUrl without a bbox omits the envelope filter", () => {
  const url = new URL(buildFeatureQueryUrl(EsriDamageFeatureRoot, 0, { where: "1=1", bbox: null, inSr: null, maxRecords: 5 }));
  assert.equal(url.searchParams.get("geometry"), null);
  assert.equal(url.searchParams.get("inSR"), null);
  assert.equal(url.searchParams.get("resultRecordCount"), "5");
});

test("buildFeatureCountUrl asks for the count shape only", () => {
  const url = new URL(
    buildFeatureCountUrl(EsriDamageFeatureRoot, 0, { where: "1=1", bbox: parseBbox("-125,25,-65,50")!, inSr: 4326 }),
  );
  assert.equal(url.searchParams.get("returnCountOnly"), "true");
  assert.equal(url.searchParams.get("geometry"), "-125,25,-65,50");
  assert.equal(url.searchParams.get("f"), "json");
});

test("localServiceRoots points both panels at the host's public GeoServices surface", () => {
  const roots = localServiceRoots("http://127.0.0.1:5199/", "WorldReference");
  assert.equal(roots.map, "http://127.0.0.1:5199/arcgis/rest/services/WorldReference/MapServer");
  assert.equal(roots.feature, "http://127.0.0.1:5199/arcgis/rest/services/WorldReference/FeatureServer");
  assert.equal(roots.image, "http://127.0.0.1:5199/arcgis/rest/services/WorldReference/ImageServer");
});

test("localGeometryRoot points the playground at the host's Geometry Service", () => {
  assert.equal(
    localGeometryRoot("http://127.0.0.1:5199/"),
    "http://127.0.0.1:5199/arcgis/rest/services/Geometry/GeometryServer",
  );
});

test("buildImageExportUrl targets exportImage with the same bbox, sr and size", () => {
  const url = new URL(buildImageExportUrl(EsriImageRoot, parseBbox("1440000,535000,1455000,550000")!, 2264, { width: 800, height: 600 }));
  assert.equal(`${url.origin}${url.pathname}`, `${EsriImageRoot}/exportImage`);
  assert.equal(url.searchParams.get("bbox"), "1440000,535000,1455000,550000");
  assert.equal(url.searchParams.get("bboxSR"), "2264");
  assert.equal(url.searchParams.get("imageSR"), "2264");
  assert.equal(url.searchParams.get("size"), "800,600");
  assert.equal(url.searchParams.get("f"), "image");
});

test("the default image bbox parses and the Esri image root names CharlotteLAS", () => {
  assert.notEqual(parseBbox(ParityDefaults.imageBbox), null);
  assert.equal(ParityDefaults.imageSr, 2264);
  assert.ok(EsriImageRoot.endsWith("/CharlotteLAS/ImageServer"));
});

test("the playground covers every host-served geometry operation with a sample", () => {
  const served = ["project", "generalize", "buffer", "intersect", "areasAndLengths", "lengths", "distance", "convexHull", "difference", "union", "simplify", "relation", "densify", "labelPoints", "findTransformations"];
  for (const operation of served) {
    assert.ok((GeometryOperations as readonly string[]).includes(operation), `missing ${operation}`);
    const sample = GeometrySamples[operation as keyof typeof GeometrySamples];
    assert.ok(sample.length > 0, `missing sample for ${operation}`);
    const url = new URL(buildGeometryUrl(EsriGeometryRoot, operation, sample)!);
    assert.equal(`${url.origin}${url.pathname}`, `${EsriGeometryRoot}/${operation}`);
    assert.equal(url.searchParams.get("f"), "json");
  }
});

test("buildGeometryUrl forces f=json and blanks out to null", () => {
  const url = new URL(buildGeometryUrl(EsriGeometryRoot, "project", "inSR=4326&outSR=3857&f=html")!);
  assert.equal(url.searchParams.get("f"), "json");
  assert.equal(buildGeometryUrl(EsriGeometryRoot, "project", "   "), null);
});

test("prettyJson canonicalises answers and keeps parse failures a value", () => {
  const pretty = prettyJson('{"b":1,"a":2}');
  assert.equal(pretty.ok, true);
  assert.equal(pretty.ok && pretty.pretty, '{\n  "b": 1,\n  "a": 2\n}');
  const bad = prettyJson("<html>nope</html>");
  assert.equal(bad.ok, false);
});

test("diffJsonLines marks identical answers clean and surfaces both sides", () => {
  const clean = diffJsonLines('a\nb', 'a\nb');
  assert.ok(diffIsClean(clean));
  const dirty = diffJsonLines('{\n  "x": 1\n}', '{\n  "x": 2\n}');
  assert.ok(!diffIsClean(dirty));
  assert.deepEqual(dirty.map((line) => line.kind), ["same", "left", "right", "same"]);
  assert.ok(dirty.some((line) => line.kind === "left" && line.text.includes("1")));
  assert.ok(dirty.some((line) => line.kind === "right" && line.text.includes("2")));
});

test("diffJsonLines falls back to a block change on oversized answers", () => {
  const big = Array.from({ length: 2500 }, (_, i) => `line ${i}`).join("\n");
  const fallback = diffJsonLines(big, `${big}\nline 2500`);
  assert.ok(!diffIsClean(fallback));
  assert.equal(fallback.length, 2);
});

test("formatBbox round-trips through parseBbox", () => {
  assert.equal(formatBbox(parseBbox(ParityDefaults.bbox)!), ParityDefaults.bbox);
});
