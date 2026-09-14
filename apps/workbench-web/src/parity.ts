/**
 * Visual parity harness (T-072/T-073): pure helpers for the workbench Parity
 * page. The page renders public Esri and localhost panels side by side for
 * the same bounding box — MapServer/export and ImageServer/exportImage
 * images on the Map and Image tabs, FeatureServer/query counts and sample
 * attributes on the Feature tab — plus a Geometry playground that runs one
 * Esri-docs-style Geometry Service query against both roots and diffs the
 * answers.
 *
 * This module holds no spatial logic: it parses "minX,miny,maxX,maxY" text
 * and builds GeoServices REST URLs. Fetching and `<img>` rendering stay in
 * the screen; rendering and querying stay on the host.
 */

/** A bounding box shared by the Esri and localhost panels. */
export interface ParityBbox {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

/** A requested export image size in pixels. */
export interface ParitySize {
  width: number;
  height: number;
}

/** The public Esri reference services (sampleserver6 ground truth). */
export const EsriCensusMapRoot =
  "https://sampleserver6.arcgisonline.com/arcgis/rest/services/Census/MapServer";
export const EsriDamageFeatureRoot =
  "https://sampleserver6.arcgisonline.com/arcgis/rest/services/DamageAssessment/FeatureServer";
/** The public Esri Geometry Service backing the playground's left panel. */
export const EsriGeometryRoot =
  "https://sampleserver6.arcgisonline.com/arcgis/rest/services/Utilities/Geometry/GeometryServer";
/** The public Esri ImageServer backing the Image tab's left panel (Charlotte lidar). */
export const EsriImageRoot =
  "https://sampleserver6.arcgisonline.com/arcgis/rest/services/CharlotteLAS/ImageServer";

/**
 * The page defaults. The bbox covers the contiguous United States: inside
 * the Census MapServer full extent (ground-truth fullExtent
 * -179.6,17.9,-65.2,71.4) and inside the world-extent T-066A seed services
 * (`WorldReference` map, `WorldCountries` features).
 */
export const ParityDefaults = {
  bbox: "-125,25,-66,50",
  sr: 4326,
  size: "800,600",
  localMap: "WorldReference",
  esriMap: EsriCensusMapRoot,
  localFeature: "WorldCountries",
  localLayer: 0,
  esriFeature: EsriDamageFeatureRoot,
  esriLayer: 0,
  localImage: "WorldReference",
  esriImage: EsriImageRoot,
  /** The CharlotteLAS native extent, for the Image tab preset (WKID 2264 feet). */
  imageBbox: "1440000,535000,1455000,550000",
  imageSr: 2264,
  where: "1=1",
  maxRecords: 20,
} as const;

/** Parses "minX,miny,maxX,maxY" (commas and/or whitespace separated); null when malformed. */
export function parseBbox(text: string): ParityBbox | null {
  const parts = text.split(/[\s,]+/).filter((part) => part !== "");
  if (parts.length !== 4) return null;
  const numbers = parts.map(Number);
  if (numbers.some((value) => !Number.isFinite(value))) return null;
  const [minX, minY, maxX, maxY] = numbers as [number, number, number, number];
  if (minX >= maxX || minY >= maxY) return null;
  return { minX, minY, maxX, maxY };
}

/** Parses "width,height" in pixels; null when malformed. */
export function parseSize(text: string): ParitySize | null {
  const parts = text.split(/[\s,]+/).filter((part) => part !== "");
  if (parts.length !== 2) return null;
  const numbers = parts.map(Number);
  if (numbers.some((value) => !Number.isFinite(value))) return null;
  const [width, height] = numbers as [number, number];
  if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0) return null;
  return { width, height };
}

/** Formats a bbox the way the export/query endpoints take it. */
export function formatBbox(bbox: ParityBbox): string {
  return `${bbox.minX},${bbox.minY},${bbox.maxX},${bbox.maxY}`;
}

/**
 * Builds a MapServer/export URL returning raw image bytes (`f=image`, so
 * the panels can use plain `<img>` tags with no CORS fetch): the same bbox,
 * spatial reference and size on both sides.
 */
export function buildMapExportUrl(
  serviceRoot: string,
  bbox: ParityBbox,
  sr: number,
  size: ParitySize,
): string {
  const params = new URLSearchParams({
    bbox: formatBbox(bbox),
    bboxSR: String(sr),
    imageSR: String(sr),
    size: `${size.width},${size.height}`,
    format: "png",
    f: "image",
  });
  return `${serviceRoot.replace(/\/+$/, "")}/export?${params.toString()}`;
}

/** Builds a FeatureServer layer query URL returning JSON (`f=json`). */
export function buildFeatureQueryUrl(
  serviceRoot: string,
  layer: number,
  options: { where: string; bbox: ParityBbox | null; inSr: number | null; maxRecords: number },
): string {
  const params = new URLSearchParams({
    where: options.where === "" ? "1=1" : options.where,
    outFields: "*",
    returnGeometry: "false",
    resultRecordCount: String(options.maxRecords),
    f: "json",
  });
  if (options.bbox !== null) {
    params.set("geometry", formatBbox(options.bbox));
    params.set("geometryType", "esriGeometryEnvelope");
    params.set("spatialRel", "esriSpatialRelIntersects");
    if (options.inSr !== null) params.set("inSR", String(options.inSr));
  }
  return `${serviceRoot.replace(/\/+$/, "")}/${layer}/query?${params.toString()}`;
}

/** Builds the matching count-only query (`returnCountOnly=true`). */
export function buildFeatureCountUrl(
  serviceRoot: string,
  layer: number,
  options: { where: string; bbox: ParityBbox | null; inSr: number | null },
): string {
  const params = new URLSearchParams({
    where: options.where === "" ? "1=1" : options.where,
    returnCountOnly: "true",
    f: "json",
  });
  if (options.bbox !== null) {
    params.set("geometry", formatBbox(options.bbox));
    params.set("geometryType", "esriGeometryEnvelope");
    params.set("spatialRel", "esriSpatialRelIntersects");
    if (options.inSr !== null) params.set("inSR", String(options.inSr));
  }
  return `${serviceRoot.replace(/\/+$/, "")}/${layer}/query?${params.toString()}`;
}

/**
 * Builds an ImageServer/exportImage URL returning raw image bytes (`f=image`,
 * so the panels can use plain `<img>` tags with no CORS fetch): the same
 * bbox, spatial reference and size on both sides.
 */
export function buildImageExportUrl(
  serviceRoot: string,
  bbox: ParityBbox,
  sr: number,
  size: ParitySize,
): string {
  const params = new URLSearchParams({
    bbox: formatBbox(bbox),
    bboxSR: String(sr),
    imageSR: String(sr),
    size: `${size.width},${size.height}`,
    format: "png",
    f: "image",
  });
  return `${serviceRoot.replace(/\/+$/, "")}/exportImage?${params.toString()}`;
}

/**
 * The Geometry Service operations the host satisfies (spec §7), offered in
 * the playground dropdown. `fromGeoCoordinateString`/`toGeoCoordinateString`
 * are deliberately absent: the engine has no coordinate-notation codec.
 */
export const GeometryOperations = [
  "project",
  "buffer",
  "generalize",
  "simplify",
  "intersect",
  "union",
  "difference",
  "convexHull",
  "densify",
  "areasAndLengths",
  "lengths",
  "distance",
  "labelPoints",
  "relation",
  "findTransformations",
] as const;

export type GeometryOperation = (typeof GeometryOperations)[number];

/**
 * Paste-ready Esri-docs-style query strings (everything after the `?`) per
 * playground operation. The samples use the same `-117,34` point the live
 * refresh probe projects, so the playground's first run matches a shape the
 * repo already asserts against the public service.
 */
export const GeometrySamples: Record<GeometryOperation, string> = {
  project:
    `geometries={"geometryType":"esriGeometryPoint","geometries":[{"x":-117,"y":34}]}` +
    `&inSR=4326&outSR=3857&f=json`,
  buffer:
    `geometries={"geometryType":"esriGeometryPoint","geometries":[{"x":-117,"y":34}]}` +
    `&inSR=4326&outSR=4326&distances=10&unit=9036&unionResults=false&geodesic=true&f=json`,
  generalize:
    `geometries=[{"paths":[[[-117,34],[-116,34],[-116,33]]]}]` +
    `&sr=4326&maxDeviation=0.01&deviationUnit=9036&f=json`,
  simplify:
    `geometries=[{"rings":[[[-117,34],[-116,34],[-116,33],[-117,34]]]}]` +
    `&sr=4326&f=json`,
  intersect:
    `geometries=[{"rings":[[[-117,34],[-116,34],[-116,33],[-117,34]]]}]` +
    `&geometry={"rings":[[[-116.5,34.5],[-115.5,34.5],[-115.5,33.5],[-116.5,34.5]]]}` +
    `&sr=4326&f=json`,
  union:
    `geometries=[{"rings":[[[-117,34],[-116,34],[-116,33],[-117,34]]]}` +
    `,"rings":[[[-116.5,34.5],[-115.5,34.5],[-115.5,33.5],[-116.5,34.5]]]}]` +
    `&sr=4326&f=json`,
  difference:
    `geometries=[{"rings":[[[-117,34],[-116,34],[-116,33],[-117,34]]]}]` +
    `&geometry={"rings":[[[-116.5,34.5],[-115.5,34.5],[-115.5,33.5],[-116.5,34.5]]]}` +
    `&sr=4326&f=json`,
  convexHull:
    `geometries=[{"x":-117,"y":34},{"x":-116,"y":33}]&sr=4326&f=json`,
  densify:
    `geometries=[{"paths":[[[-117,34],[-116,34]]]}]` +
    `&sr=4326&maxSegmentLength=0.1&lengthUnit=9036&geodesic=false&f=json`,
  areasAndLengths:
    `polygons=[{"rings":[[[-117,34],[-116,34],[-116,33],[-117,34]]]}]` +
    `&sr=4326&lengthUnit=9036&areaUnit=9036&calculationType=planar&f=json`,
  lengths:
    `polylines=[{"paths":[[[-117,34],[-116,34]]]}]` +
    `&sr=4326&lengthUnit=9036&calculationType=planar&geodesic=false&f=json`,
  distance:
    `geometry1={"x":-117,"y":34}&geometry2={"x":-116,"y":33}` +
    `&sr=4326&distanceUnit=9036&geodesic=false&f=json`,
  labelPoints:
    `polygons=[{"rings":[[[-117,34],[-116,34],[-116,33],[-117,34]]]}]&sr=4326&f=json`,
  relation:
    `geometries1=[{"x":-117,"y":34}]&geometries2=[{"rings":[[[-118,35],[-116,35],[-116,33],[-118,35]]]}]` +
    `&sr1=4326&sr2=4326&relation=esriSpatialRelContains&f=json`,
  findTransformations: `inSR=4326&outSR=3857&f=json`,
};

/**
 * Builds a Geometry Service operation URL (`f=json` forced): the playground
 * pastes one Esri-docs-style query string and runs it against both roots.
 * Blank input yields null so the screen can ask for parameters first.
 */
export function buildGeometryUrl(
  serviceRoot: string,
  operation: string,
  queryText: string,
): string | null {
  if (queryText.trim() === "") return null;
  const params = new URLSearchParams(queryText.trim().replace(/^\?/, ""));
  params.set("f", "json");
  return `${serviceRoot.replace(/\/+$/, "")}/${operation}?${params.toString()}`;
}

/** Pretty-prints a service answer canonically; parse failures stay a value, not a throw. */
export function prettyJson(text: string): { ok: true; pretty: string } | { ok: false; error: string } {
  try {
    return { ok: true, pretty: JSON.stringify(JSON.parse(text), null, 2) };
  } catch {
    return { ok: false, error: "the answer was not JSON — the service may have returned an error page" };
  }
}

export type JsonDiffLine =
  | { kind: "same"; text: string }
  | { kind: "left"; text: string }
  | { kind: "right"; text: string };

/**
 * Line diff of two pretty-printed answers (classic LCS, inputs are small
 * service answers). Oversized pairs (over ~4M cells) fall back to a
 * whole-block change so the tab never hangs on a giant buffer polygon.
 */
export function diffJsonLines(left: string, right: string): JsonDiffLine[] {
  const a = left.split("\n");
  const b = right.split("\n");
  if (a.length * b.length > 4_000_000) {
    return [
      { kind: "left", text: `… ${a.length} lines (too large for a line diff)` },
      { kind: "right", text: `… ${b.length} lines (too large for a line diff)` },
    ];
  }
  const widths = new Uint32Array((a.length + 1) * (b.length + 1));
  const at = (i: number, j: number): number => widths[i * (b.length + 1) + j]!;
  for (let i = a.length - 1; i >= 0; i--) {
    for (let j = b.length - 1; j >= 0; j--) {
      widths[i * (b.length + 1) + j] =
        a[i] === b[j] ? at(i + 1, j + 1) + 1 : Math.max(at(i + 1, j), at(i, j + 1));
    }
  }
  const lines: JsonDiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      lines.push({ kind: "same", text: a[i]! });
      i++;
      j++;
    } else if (at(i + 1, j) >= at(i, j + 1)) {
      lines.push({ kind: "left", text: a[i]! });
      i++;
    } else {
      lines.push({ kind: "right", text: b[j]! });
      j++;
    }
  }
  while (i < a.length) lines.push({ kind: "left", text: a[i++]! });
  while (j < b.length) lines.push({ kind: "right", text: b[j++]! });
  return lines;
}

/** True when the line diff holds no left/right changes. */
export function diffIsClean(lines: JsonDiffLine[]): boolean {
  return lines.every((line) => line.kind === "same");
}
export function localServiceRoots(hostBaseUrl: string, service: string): { map: string; feature: string; image: string } {
  const root = `${hostBaseUrl.replace(/\/+$/, "")}/arcgis/rest/services/${service}`;
  return { map: `${root}/MapServer`, feature: `${root}/FeatureServer`, image: `${root}/ImageServer` };
}

/** The localhost Geometry Service root, under the host the workbench talks to. */
export function localGeometryRoot(hostBaseUrl: string): string {
  return `${hostBaseUrl.replace(/\/+$/, "")}/arcgis/rest/services/Geometry/GeometryServer`;
}
