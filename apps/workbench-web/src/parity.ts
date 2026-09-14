/**
 * Visual parity harness (T-072): pure helpers for the workbench Parity
 * page. The page renders public Esri and localhost panels side by side for
 * the same bounding box — MapServer/export images on the Map tab,
 * FeatureServer/query counts and sample attributes on the Feature tab.
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

/** The localhost GeoServices roots for a seed service, under the host the workbench talks to. */
export function localServiceRoots(hostBaseUrl: string, service: string): { map: string; feature: string } {
  const root = `${hostBaseUrl.replace(/\/+$/, "")}/arcgis/rest/services/${service}`;
  return { map: `${root}/MapServer`, feature: `${root}/FeatureServer` };
}
