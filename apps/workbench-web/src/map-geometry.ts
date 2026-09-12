/**
 * Pure projection helpers shared by the map screens. No renderer reads and
 * no spatial algorithms: this is only the flattening of GeoJSON coordinates
 * that MapLibre's own `LngLatBounds` needs to fit a collection.
 */

/** Appends every `[lng, lat]` coordinate in a geometry to `target`. */
export function collectCoordinates(target: [number, number][], geometry: GeoJSON.Geometry | null): void {
  if (geometry === null) return;
  switch (geometry.type) {
    case "Point":
      target.push(geometry.coordinates as [number, number]);
      break;
    case "MultiPoint":
    case "LineString":
      (geometry.coordinates as number[][]).forEach((point) => target.push(point as [number, number]));
      break;
    case "MultiLineString":
    case "Polygon":
      (geometry.coordinates as number[][][]).forEach((ring) => ring.forEach((point) => target.push(point as [number, number])));
      break;
    case "MultiPolygon":
      (geometry.coordinates as number[][][][]).forEach((polygon) => polygon.forEach((ring) => ring.forEach((point) => target.push(point as [number, number]))));
      break;
    case "GeometryCollection":
      geometry.geometries.forEach((child) => collectCoordinates(target, child));
      break;
  }
}

/** Flattens a feature collection into the coordinates its bounds span. */
export function collectionCoordinates(collection: GeoJSON.FeatureCollection<GeoJSON.Geometry | null>): [number, number][] {
  const coordinates: [number, number][] = [];
  for (const feature of collection.features ?? []) collectCoordinates(coordinates, feature.geometry);
  return coordinates;
}
