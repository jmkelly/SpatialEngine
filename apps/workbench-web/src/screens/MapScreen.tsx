import { useEffect, useRef, useState } from "react";
import { Map as MapLibreMap, NavigationControl, LngLatBounds, setWorkerUrl, type GeoJSONSource, type StyleSpecification } from "maplibre-gl";
import "maplibre-gl/dist/maplibre-gl.css";
import { useWorkbench } from "../state.tsx";
import { lngSpanPixels, nearestFeatureId } from "../click-match.ts";

// Maplibre computes its worker URL from its bundle location; under a static
// SPA build the worker must be served next to the app, so pin it to the
// workbench root (public/maplibre-gl-worker.mjs). Without a reachable worker
// the map never finishes loading.
if (typeof window !== "undefined") {
  setWorkerUrl("/maplibre-gl-worker.mjs");
}

/** The background raster behind engine data. `?basemap=none` (or the stored choice) keeps the offline dark canvas. */
export type Basemap = "dark" | "light" | "none";

const BasemapStorageKey = "spatial:basemap";

function initialBasemap(): Basemap {
  try {
    const param = new URLSearchParams(window.location.search).get("basemap");
    if (param === "dark" || param === "light" || param === "none") return param;
    const stored = window.localStorage.getItem(BasemapStorageKey);
    if (stored === "dark" || stored === "light" || stored === "none") return stored;
  } catch {
    // Storage or URL access can fail in locked-down browsers — fall back to the dark map.
  }
  return "dark";
}

/**
 * The workbench map (Phase 10): a MapLibre canvas over a CARTO raster
 * basemap (toggleable to light or to the offline dark canvas), with the
 * loaded dataset as clustered point/line/fill layers and the
 * invocation/persisted-result layer in a contrasting colour. Geometry
 * attributes are canonical SGEOM bytes decoded by src/sgeom.ts; the client
 * holds no spatial algorithms.
 */
export function MapScreen() {
  const container = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<MapLibreMap | null>(null);
  const appliedBasemap = useRef<Basemap | null>(null);
  const [basemap, setBasemap] = useState<Basemap>(initialBasemap);
  const { geojson, resultGeojson, selectedFeatureId, selectedProperties, catalogueDatasets, selectedDataset, actions } = useWorkbench();

  // Transient operation previews are tagged `kind: "operation"`; saved results
  // (`kind: "saved"`) are kept when clearing.
  const resultCount = resultGeojson.features.length;
  const unsavedResultCount = resultGeojson.features.filter((feature) => feature.properties?.kind === "operation").length;

  // The map effect runs once (mount) but must call the *current* select
  // action: the initial closure would look selections up in the initial
  // empty dataset forever. A ref keeps the latest actions without
  // recreating the map.
  const actionsRef = useRef(actions);
  actionsRef.current = actions;
  const featuresRef = useRef(geojson.features);
  featuresRef.current = geojson.features;
  const resultsRef = useRef(resultGeojson);
  resultsRef.current = resultGeojson;

  // One map for the screen's lifetime: sources and layers are declared in
  // the style so there is no load-event race (MapLibre programmatic
  // addSource/addLayer before full load leaves the renderer waiting).
  useEffect(() => {
    if (container.current === null || mapRef.current !== null) return;
    const map = new MapLibreMap({
      container: container.current,
      style: basemapStyle(initialBasemap()),
      center: [0, 20],
      zoom: 1.4,
      attributionControl: { compact: true },
    });
    appliedBasemap.current = initialBasemap();
    mapRef.current = map;
    // Exposed so browser tests can inspect renderer state (and for debugging).
    (window as unknown as { __spatialMap?: MapLibreMap }).__spatialMap = map;
    map.addControl(new NavigationControl({ showCompass: false }), "top-right");

    // Selection is coordinate-based (click lng/lat -> nearest feature)
    // rather than pixel-query based: readPixels is fragile in software-
    // rendered headless browsers, while projection math is deterministic.
    // Clicking empty space clears the selection.
    const selectNear = (lngLat: { lng: number; lat: number }): boolean => {
      const span = lngSpanPixels(map.getZoom(), 16);
      const chosen = nearestFeatureId(featuresRef.current, lngLat, span);
      actionsRef.current.selectFeature(chosen);
      return chosen !== null;
    };
    map.on("click", (event) => {
      const selected = selectNear(event.lngLat);
      if (selected) return;
      // The dataset can still be settling after a fresh load; retry once
      // when the map becomes idle before clearing the selection.
      map.once("idle", () => void selectNear(event.lngLat));
    });
    map.on("mousemove", (event) => {
      const found = map.queryRenderedFeatures(event.point, { layers: ["clusters", "feature-points", "feature-lines", "feature-fills"] });
      map.getCanvas().style.cursor = found.length > 0 ? "pointer" : "";
    });

    return () => undefined;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // A basemap switch replaces the whole style (sources included), so the
  // current dataset and result layers are re-seeded once it is ready.
  useEffect(() => {
    const map = mapRef.current;
    if (map === null || appliedBasemap.current === basemap) return;
    appliedBasemap.current = basemap;
    try {
      window.localStorage.setItem(BasemapStorageKey, basemap);
    } catch {
      // Locked-down browsers may refuse storage — the map still switches.
    }
    map.setStyle(basemapStyle(basemap));
    map.once("style.load", () => {
      updateSource(map, "features", { type: "FeatureCollection", features: featuresRef.current });
      updateSource(map, "results", resultsRef.current);
    });
  }, [basemap]);

  // Dataset features change -> swap the source data and refit.
  useEffect(() => {
    const map = mapRef.current;
    if (map === null) return;
    updateSource(map, "features", geojson);
    fitFeatures(map);
  }, [geojson]);

  // Result/preview layer changes.
  useEffect(() => {
    const map = mapRef.current;
    if (map === null) return;
    updateSource(map, "results", resultGeojson);
    if (resultGeojson.features.length > 0) fitFeatures(map);
  }, [resultGeojson]);

  return (
    <section className="screen map-screen">
      <div className="map-toolbar">
        <label htmlFor="dataset-select">Dataset</label>
        <select
          id="dataset-select"
          data-testid="dataset-select"
          value={selectedDataset ?? ""}
          onChange={(event) => { const value = event.target.value; if (value) void actions.loadDataset(value); }}
        >
          <option value="" disabled>{catalogueDatasets.length === 0 ? "no catalogue provider — pick a demo dataset" : "choose a dataset…"}</option>
          {datasetOptions(catalogueDatasets).map((id) => (
            <option key={id} value={id}>{id}</option>
          ))}
        </select>
        <label htmlFor="basemap-select">Basemap</label>
        <select
          id="basemap-select"
          data-testid="basemap-select"
          value={basemap}
          onChange={(event) => setBasemap(event.target.value as Basemap)}
        >
          <option value="dark">dark</option>
          <option value="light">light</option>
          <option value="none">none</option>
        </select>
        <button className="ghost" onClick={() => actions.selectFeature(null)}>clear selection</button>
        <button
          className="ghost"
          data-testid="clear-results"
          onClick={() => actions.clearResults()}
          disabled={unsavedResultCount === 0}
        >
          clear results
        </button>
        <span className="muted" data-testid="result-count">{resultCount} result{resultCount === 1 ? "" : "s"}</span>
        <span className="muted" data-testid="feature-count">{geojson.features.length} features</span>
        <span className="muted">cities © GeoNames CC-BY 4.0{basemap === "none" ? null : " · basemap © OpenStreetMap © Esri"}</span>
      </div>

      <div className="map-wrap">
        <div ref={container} className="map" data-testid="map" />
        <aside className="selection-panel" data-testid="selection">
          {selectedProperties === null ? (
            <p className="muted">Click a feature on the map to inspect its attributes and use it in a capability form.</p>
          ) : (
            <>
              <h4>Selected {selectedFeatureId !== null ? <code>{selectedFeatureId}</code> : null}</h4>
              <dl className="attributes">
                {Object.entries(selectedProperties).map(([name, value]) => (
                  <div key={name}><dt>{name}</dt><dd>{attributeDisplay(value)}</dd></div>
                ))}
              </dl>
            </>
          )}
        </aside>
      </div>
    </section>
  );
}

function datasetOptions(catalogue: string[]): string[] {
  if (catalogue.length > 0) return catalogue;
  return ["demo.points", "demo.cities", "demo.world_cities"];
}

function basemapSource(basemap: Basemap): Record<string, { type: "raster"; tiles: string[]; tileSize: number; maxzoom: number; attribution: string }> {
  // Esri's Canvas gray basemaps need no API key (CARTO's free tiles now
  // print an "API KEY REQUIRED" watermark). Note Esri's {z}/{y}/{x} order.
  if (basemap === "none") return {};
  const service = basemap === "dark" ? "Canvas/World_Dark_Gray_Base" : "Canvas/World_Light_Gray_Base";
  return {
    basemap: {
      type: "raster",
      tiles: [`https://server.arcgisonline.com/ArcGIS/rest/services/${service}/MapServer/tile/{z}/{y}/{x}`],
      tileSize: 256,
      maxzoom: 16,
      attribution: "© OpenStreetMap contributors © Esri",
    },
  };
}

function basemapStyle(basemap: Basemap): StyleSpecification {
  return {
    version: 8,
    sources: {
      ...basemapSource(basemap),
      features: {
        type: "geojson",
        data: { type: "FeatureCollection", features: [] } as GeoJSON.FeatureCollection,
        cluster: true,
        clusterMaxZoom: 12,
        clusterRadius: 50,
      },
      results: { type: "geojson", data: { type: "FeatureCollection", features: [] } as GeoJSON.FeatureCollection },
    },
    layers: [
      { id: "background", type: "background", paint: { "background-color": "#10151c" } },
      ...(basemap === "none"
        ? []
        : [{ id: "basemap", type: "raster" as const, source: "basemap", paint: { "raster-opacity": 0.9 } }]),
      {
        id: "clusters",
        type: "circle",
        source: "features",
        filter: ["has", "point_count"],
        paint: {
          "circle-color": ["step", ["get", "point_count"], "#4fc3f7", 100, "#29b6f6", 750, "#0277bd"],
          "circle-radius": ["step", ["get", "point_count"], 14, 100, 20, 750, 26],
          "circle-stroke-color": "#0b0f14",
          "circle-stroke-width": 1,
        },
      },
      {
        id: "cluster-count",
        type: "symbol",
        source: "features",
        filter: ["has", "point_count"],
        layout: { "text-field": "{point_count_abbreviated}", "text-size": 12 },
        paint: { "text-color": "#0b0f14" },
      },
      { id: "feature-points", type: "circle", source: "features", filter: ["!", ["has", "point_count"]], paint: { "circle-color": "#4fc3f7", "circle-radius": 5, "circle-stroke-color": "#0b0f14", "circle-stroke-width": 1 } },
      { id: "feature-lines", type: "line", source: "features", paint: { "line-color": "#4fc3f7", "line-width": 2 } },
      { id: "feature-fills", type: "fill", source: "features", paint: { "fill-color": "#4fc3f7", "fill-opacity": 0.28, "fill-outline-color": "#9adcff" } },
      { id: "result-fills", type: "fill", source: "results", paint: { "fill-color": "#ffd54f", "fill-opacity": 0.3, "fill-outline-color": "#ffecb3" } },
      { id: "result-points", type: "circle", source: "results", paint: { "circle-color": "#ffd54f", "circle-radius": 7, "circle-stroke-color": "#1a1200", "circle-stroke-width": 1.5 } },
      { id: "result-lines", type: "line", source: "results", paint: { "line-color": "#ffd54f", "line-width": 2.5 } },
    ],
  };
}

/**
 * Epochs per source: every direct setData bumps the epoch. A deferred
 * setData (queued while the style was still loading) only applies when no
 * newer data landed meanwhile — without this, a slow raster basemap lets
 * the map's late `load` event wipe a freshly loaded dataset with the
 * mount-time empty FeatureCollection.
 */
const sourceEpoch: Record<string, number> = {};

/** Sets a GeoJSON source's data when it is ready; defers past style load otherwise (guarded by the source epoch). */
function updateSource(map: MapLibreMap, sourceId: string, data: GeoJSON.FeatureCollection): void {
  const source = map.getSource(sourceId) as GeoJSONSource | undefined;
  if (source !== undefined) {
    sourceEpoch[sourceId] = (sourceEpoch[sourceId] ?? 0) + 1;
    source.setData(data);
    return;
  }
  const epoch = sourceEpoch[sourceId] ?? 0;
  map.once("load", () => {
    if ((sourceEpoch[sourceId] ?? 0) !== epoch) return;
    const ready = map.getSource(sourceId) as GeoJSONSource | undefined;
    if (ready !== undefined) ready.setData(data);
  });
}

function fitFeatures(map: MapLibreMap): void {
  const source = map.getSource("features") as GeoJSONSource | undefined;
  if (source === undefined) {
    map.once("load", () => fitFeatures(map));
    return;
  }
  const data = source.serialize()?.data;
  if (typeof data !== "object" || data === null) return;
  const features: GeoJSON.Feature[] = (data as GeoJSON.FeatureCollection).features ?? [];
  const coordinates: [number, number][] = [];
  for (const feature of features) collect(coordinates, feature.geometry);
  if (coordinates.length === 0) return;
  const bounds = new LngLatBounds(coordinates[0]!, coordinates[0]!);
  for (const [x, y] of coordinates) bounds.extend([x, y]);
  map.fitBounds(bounds, { padding: 40, maxZoom: 12, duration: 0 });
}

function collect(target: [number, number][], geometry: GeoJSON.Geometry | null): void {
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
      geometry.geometries.forEach((child) => collect(target, child));
      break;
  }
}

function attributeDisplay(value: unknown): string {
  if (value === null) return "∅";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}
