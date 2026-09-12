/**
 * Coordinate-based selection matching: the feature whose
 * representative coordinate is nearest the clicked lng/lat (within a
 * click tolerance in screen pixels). Pure projection math — deliberately
 * rendering-independent, because pixel-query selection (readPixels) is
 * fragile in software-rendered headless browsers and depends on render
 * timing. Unit-tested here without a map: callers pass the pixel span per
 * click afterwards.
 */

export interface ClickPoint {
  lng: number;
  lat: number;
}

/** The lng span (degrees) of `pixels` screen pixels at a zoom level (the web-Mercator tile scale). */
export function lngSpanPixels(zoom: number, pixels: number): number {
  return (pixels * 360) / (256 * 2 ** zoom);
}

/** The representative coordinate of a GeoJSON geometry (polygon centres). */
export function representativeCoordinate(geometry: GeoJSON.Geometry | null): [number, number] | undefined {
  if (geometry === null) return undefined;
  switch (geometry.type) {
    case "Point":
      return [geometry.coordinates[0]!, geometry.coordinates[1]!];
    case "MultiPoint":
      return firstPair(geometry.coordinates);
    case "LineString":
      return centroid(geometry.coordinates);
    case "MultiLineString":
      return centroid(geometry.coordinates.flat());
    case "Polygon":
      return centroid(geometry.coordinates[0] ?? []);
    case "MultiPolygon":
      return centroid(geometry.coordinates[0]?.[0] ?? []);
    case "GeometryCollection": {
      for (const child of geometry.geometries) {
        const coordinate = representativeCoordinate(child);
        if (coordinate !== undefined) return coordinate;
      }
      return undefined;
    }
  }
}

/**
 * The id of the feature nearest the click point, or null when nothing lies
 * within the tolerance (squared-distance ≤ 1 after normalising by the pixel
 * span in each axis, using Mercator y so latitude neighbours stay planar).
 */
export function nearestFeatureId(
  features: GeoJSON.Feature[],
  point: ClickPoint,
  spanPerPixel: number,
): string | null {
  let best: { id: string; distanceSq: number } | null = null;
  for (const feature of features) {
    const coordinate = representativeCoordinate(feature.geometry);
    if (coordinate === undefined) continue;
    const dx = (coordinate[0] - point.lng) / spanPerPixel;
    const dy = (mercatorY(coordinate[1]) - mercatorY(point.lat)) / spanPerPixel;
    const distanceSq = dx * dx + dy * dy;
    if (distanceSq > 1) continue;
    if (best === null || distanceSq < best.distanceSq) {
      const id = feature.id !== undefined && feature.id !== null
        ? String(feature.id)
        : String((feature.properties as Record<string, unknown> | null | undefined)?.["__fid"] ?? "");
      if (id.length > 0) best = { id, distanceSq };
    }
  }
  return best?.id ?? null;
}

function firstPair(coordinates: number[][]): [number, number] | undefined {
  const first = coordinates[0];
  if (first === undefined) return undefined;
  return [first[0]!, first[1]!];
}

function centroid(coordinates: number[][]): [number, number] | undefined {
  if (coordinates.length === 0) return undefined;
  const sum = coordinates.reduce((acc, point) => [acc[0] + point[0]!, acc[1] + point[1]!], [0, 0]);
  return [sum[0]! / coordinates.length, sum[1]! / coordinates.length];
}

/** Normalised Mercator y (0..1) for a latitude. */
export function mercatorY(lat: number): number {
  const clamped = Math.max(-89.9, Math.min(89.9, lat));
  const rad = (clamped * Math.PI) / 180;
  return (1 - Math.log(Math.tan(Math.PI / 4 + rad / 2)) / Math.PI) / 2;
}