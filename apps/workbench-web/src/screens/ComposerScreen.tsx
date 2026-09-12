import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Map as MapLibreMap, NavigationControl, LngLatBounds, setWorkerUrl, type GeoJSONSource, type StyleSpecification } from "maplibre-gl";
import "maplibre-gl/dist/maplibre-gl.css";
import type { DatasetSummary, FeatureBatch, Publication, PublicationKind } from "@spatial/client";
import { createClient } from "../api.ts";
import { batchToGeoJson } from "../sgeom.ts";
import { basemapSource, initialBasemap, rememberBasemap, type Basemap } from "../basemap.ts";
import { collectionCoordinates } from "../map-geometry.ts";
import {
  addLayer,
  defaultStyle,
  fromPublication,
  geometryTypesOf,
  inferGeometryKind,
  layerSpecs,
  newLayerId,
  removeLayer,
  reorderLayers,
  sourceId,
  toPublication,
  updateLayerName,
  updateLayerStyle,
  type ComposerLayer,
  type LayerStyle,
} from "../composer.ts";

// Same worker pin as the Map screen: without a reachable worker the map never
// finishes loading. The call is idempotent across module instances.
if (typeof window !== "undefined") {
  setWorkerUrl("/maplibre-gl-worker.mjs");
}

const EmptyCollection: GeoJSON.FeatureCollection = { type: "FeatureCollection", features: [] };

/**
 * The map composer (`architecture/map-composer-plan.md`): add catalogue
 * datasets or uploaded files as ordered, styled layers, preview them on
 * MapLibre, and publish the composition as a feature or map service. The
 * screen talks only to the public host API through the TS SDK; per-layer
 * style is authoring state (the published publication carries ordered layers
 * and stable ids only).
 */
export function ComposerScreen() {
  const client = useMemo(() => createClient(), []);
  const container = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<MapLibreMap | null>(null);
  const appliedBasemap = useRef<Basemap | null>(null);

  const [basemap, setBasemap] = useState<Basemap>(initialBasemap);
  const [store, setStore] = useState("demo");
  const [name, setName] = useState("draft_service");
  const [kind, setKind] = useState<PublicationKind>("feature");
  const [layers, setLayers] = useState<ComposerLayer[]>([]);
  const [features, setFeatures] = useState<Record<string, GeoJSON.FeatureCollection>>({});
  const [catalogue, setCatalogue] = useState<DatasetSummary[]>([]);
  const [publications, setPublications] = useState<Publication[]>([]);
  const [selectedDataset, setSelectedDataset] = useState("");
  const [token, setToken] = useState("");
  const [uploadFile, setUploadFile] = useState<File | null>(null);
  const [uploadDataset, setUploadDataset] = useState("public.upload");
  const [format, setFormat] = useState("geojson");
  const [srid, setSrid] = useState("4326");
  const [expanded, setExpanded] = useState<string | null>(null);
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  const dragIndexRef = useRef<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  // The mount-once map effect must apply the latest state; refs keep the
  // sync callback independent of render closures.
  const syncRef = useRef<{ layers: ComposerLayer[]; features: Record<string, GeoJSON.FeatureCollection> }>({ layers, features });
  syncRef.current = { layers, features };
  const pendingSync = useRef(false);
  const appliedSpecs = useRef<string[]>([]);
  const appliedSources = useRef<string[]>([]);

  const scheduleSync = useCallback(() => {
    const map = mapRef.current;
    if (map === null) return;
    if (map.isStyleLoaded()) {
      applyComposerLayers(map, syncRef.current.layers, syncRef.current.features, appliedSpecs, appliedSources);
      return;
    }

    if (pendingSync.current) return;
    pendingSync.current = true;
    map.once("idle", () => {
      pendingSync.current = false;
      const current = mapRef.current;
      if (current !== null) applyComposerLayers(current, syncRef.current.layers, syncRef.current.features, appliedSpecs, appliedSources);
    });
  }, []);

  useEffect(() => {
    if (container.current === null || mapRef.current !== null) return;
    const map = new MapLibreMap({
      container: container.current,
      style: composerStyle(initialBasemap()),
      center: [0, 20],
      zoom: 1.4,
      attributionControl: { compact: true },
    });
    appliedBasemap.current = initialBasemap();
    mapRef.current = map;
    (window as unknown as { __spatialComposerMap?: MapLibreMap }).__spatialComposerMap = map;
    map.addControl(new NavigationControl({ showCompass: false }), "top-right");
    map.on("load", scheduleSync);

    return () => {
      map.remove();
      mapRef.current = null;
      appliedBasemap.current = null;
      appliedSpecs.current = [];
      appliedSources.current = [];
    };
  }, [scheduleSync]);

  // Layer membership, order or style changed -> resync the preview.
  useEffect(() => {
    scheduleSync();
  }, [layers, features, scheduleSync]);

  // A basemap switch replaces the whole style, so the composer layers are
  // re-seeded once it is ready.
  useEffect(() => {
    const map = mapRef.current;
    if (map === null || appliedBasemap.current === basemap) return;
    appliedBasemap.current = basemap;
    rememberBasemap(basemap);
    appliedSpecs.current = [];
    appliedSources.current = [];
    map.setStyle(composerStyle(basemap));
    map.once("style.load", scheduleSync);
  }, [basemap, scheduleSync]);

  const refreshCatalogue = useCallback(async (storeKey: string) => {
    try {
      setCatalogue((await client.catalogue(storeKey)).datasets);
    } catch (failure) {
      setError(messageOf(failure));
    }
  }, [client]);

  const refreshPublications = useCallback(async () => {
    try {
      setPublications(await client.listPublications());
    } catch (failure) {
      setError(messageOf(failure));
    }
  }, [client]);

  useEffect(() => {
    void refreshCatalogue(store);
  }, [store, refreshCatalogue]);

  useEffect(() => {
    void refreshPublications();
  }, [refreshPublications]);

  const loadDataset = useCallback(async (datasetId: string) => {
    if (datasetId === "") return;
    if (layers.some((layer) => layer.dataset === datasetId)) {
      setError(`${datasetId} is already in the composition.`);
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const collection = mergeBatches(await client.scan(datasetId, store));
      const geometry = inferGeometryKind(geometryTypesOf(collection));
      const layer: ComposerLayer = {
        id: newLayerId(),
        store,
        dataset: datasetId,
        name: datasetId,
        geometry,
        style: defaultStyle(geometry, layers.length),
        featureCount: collection.features.length,
        layerId: null,
      };
      setLayers((current) => addLayer(current, layer));
      setFeatures((current) => ({ ...current, [layer.id]: collection }));
      const map = mapRef.current;
      if (map !== null) fitMap(map, collection);
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  }, [client, layers, store]);

  const uploadAndAdd = useCallback(async () => {
    if (uploadFile === null) {
      setError("Choose a file to upload first.");
      return;
    }
    if (store === "demo") {
      setError("The demo store is read-only; choose memory or postgis to import a file.");
      return;
    }

    setBusy(true);
    setError(null);
    setStatus(null);
    try {
      const ingested = await client.ingest(
        uploadFile,
        uploadFile.name,
        { dataset: uploadDataset, srid: Number.parseInt(srid, 10), format, store, identity: "auto" },
        token === "" ? undefined : token,
      );
      setStatus(`Imported ${ingested.features} feature(s) into ${ingested.dataset}.`);
      await refreshCatalogue(store);
      await loadDataset(ingested.dataset);
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  }, [client, format, loadDataset, refreshCatalogue, srid, store, token, uploadDataset, uploadFile]);

  const publish = useCallback(async () => {
    if (layers.length === 0) {
      setError("Add at least one layer before publishing.");
      return;
    }

    setBusy(true);
    setError(null);
    setStatus(null);
    try {
      const stored = await client.putPublication(toPublication({ name, kind, store, layers }), token === "" ? undefined : token);
      setStatus(`Published ${stored.name} (${stored.kind}) with ${stored.layers.length} layer(s).`);
      await refreshPublications();
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  }, [client, kind, layers, name, refreshPublications, store, token]);

  const loadPublication = useCallback(async (publication: Publication) => {
    setBusy(true);
    setError(null);
    setStatus(null);
    try {
      const draft = fromPublication(publication);
      const loadedFeatures: Record<string, GeoJSON.FeatureCollection> = {};
      const hydrated = await Promise.all(draft.layers.map(async (layer) => {
        try {
          const collection = mergeBatches(await client.scan(layer.dataset, publication.store));
          loadedFeatures[layer.id] = collection;
          return { ...layer, geometry: inferGeometryKind(geometryTypesOf(collection)), featureCount: collection.features.length };
        } catch {
          // Keep the layer (and its stable id) even when its data cannot be read back.
          return layer;
        }
      }));

      setStore(publication.store);
      setName(publication.name);
      setKind(publication.kind);
      setLayers(hydrated);
      setFeatures(loadedFeatures);
      setStatus(`Loaded ${publication.name}.`);
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  }, [client]);

  const deletePublication = useCallback(async (publication: Publication) => {
    setBusy(true);
    setError(null);
    try {
      await client.deletePublication(publication.name, token === "" ? undefined : token);
      await refreshPublications();
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  }, [client, refreshPublications, token]);

  function changeStore(next: string) {
    setStore(next);
    setLayers([]);
    setFeatures({});
    setSelectedDataset("");
  }

  function removeOne(id: string) {
    setLayers((current) => removeLayer(current, id));
    setFeatures((current) => {
      const next = { ...current };
      delete next[id];
      return next;
    });
  }

  function move(index: number, delta: number) {
    setLayers((current) => reorderLayers(current, index, index + delta));
  }

  function dropAt(index: number) {
    const from = dragIndexRef.current;
    if (from === null) return;
    setLayers((current) => reorderLayers(current, from, index));
    dragIndexRef.current = null;
    setDragIndex(null);
  }

  function pickFile(file: File | null) {
    setUploadFile(file);
    if (file === null) return;
    setFormat(formatFor(file.name));
    setUploadDataset(datasetFromFileName(file.name));
  }

  return (
    <section className="screen composer-screen">
      <div className="composer-header">
        <div>
          <h2>Map composer</h2>
          <p className="muted small">
            Compose datasets into an ordered, styled map and publish it as a feature or map service.
            Style is a preview aid; the publication records the ordered layers.
          </p>
        </div>
        <label className="field compact">
          <span>Basemap</span>
          <select data-testid="composer-basemap" value={basemap} onChange={(event) => setBasemap(event.target.value as Basemap)}>
            <option value="dark">dark</option>
            <option value="light">light</option>
            <option value="none">none</option>
          </select>
        </label>
      </div>

      <div className="composer-layout">
        <div className="composer-map-wrap">
          <div ref={container} className="map composer-map" data-testid="composer-map" />
        </div>

        <aside className="composer-panel">
          <div className="form">
            <label className="field">
              <span>Admin token</span>
              <input
                type="password"
                data-testid="composer-token"
                value={token}
                placeholder="SPATIAL_ADMIN_TOKEN"
                onChange={(event) => setToken(event.target.value)}
              />
              <small>Required to publish, upload or delete; never stored.</small>
            </label>

            <div className="composer-row">
              <label className="field">
                <span>Service name</span>
                <input data-testid="composer-name" value={name} onChange={(event) => setName(event.target.value)} />
              </label>
              <label className="field">
                <span>Kind</span>
                <select data-testid="composer-kind" value={kind} onChange={(event) => setKind(event.target.value as PublicationKind)}>
                  <option value="feature">Feature</option>
                  <option value="map">Map</option>
                </select>
              </label>
            </div>

            <label className="field">
              <span>Store</span>
              <select data-testid="composer-store" value={store} onChange={(event) => changeStore(event.target.value)}>
                <option value="demo">demo (read-only)</option>
                <option value="memory">memory (ephemeral, writable)</option>
                <option value="postgis">postgis</option>
              </select>
              <small>One publication exposes one store; changing it clears the layers.</small>
            </label>

            <div className="form-actions">
              <button className="primary" data-testid="composer-publish" disabled={busy || layers.length === 0} onClick={() => void publish()}>
                Publish
              </button>
              <button className="ghost" disabled={layers.length === 0} onClick={() => { setLayers([]); setFeatures({}); }}>
                Clear layers
              </button>
            </div>
          </div>

          <h3>Layers ({layers.length})</h3>
          <ol className="layer-list" data-testid="composer-layers">
            {layers.map((layer, index) => (
              <li
                key={layer.id}
                data-testid="composer-layer"
                data-dataset={layer.dataset}
                draggable
                className={`layer-item${dragIndex === index ? " dragging" : ""}`}
                onDragStart={() => { dragIndexRef.current = index; setDragIndex(index); }}
                onDragOver={(event) => event.preventDefault()}
                onDrop={() => dropAt(index)}
                onDragEnd={() => { dragIndexRef.current = null; setDragIndex(null); }}
              >
                <div className="layer-row">
                  <span className="layer-handle" title="Drag to reorder" aria-hidden="true">⋮⋮</span>
                  <button
                    className="ghost layer-eye"
                    title={layer.style.visible ? "Hide layer" : "Show layer"}
                    aria-label={layer.style.visible ? `Hide ${layer.dataset}` : `Show ${layer.dataset}`}
                    data-testid="layer-visibility"
                    onClick={() => setLayers((current) => updateLayerStyle(current, layer.id, { visible: !layer.style.visible }))}
                  >
                    {layer.style.visible ? "◉" : "○"}
                  </button>
                  <span className="layer-swatch" style={{ background: layer.style.color }} />
                  <div className="layer-meta">
                    <input
                      className="layer-name"
                      data-testid="layer-name"
                      value={layer.name}
                      onChange={(event) => setLayers((current) => updateLayerName(current, layer.id, event.target.value))}
                    />
                    <span className="muted small">{layer.dataset} · {layer.geometry} · {layer.featureCount} feature(s)</span>
                  </div>
                  <div className="layer-actions">
                    <button className="ghost" data-testid="layer-up" title="Move up" disabled={index === 0} onClick={() => move(index, -1)}>▲</button>
                    <button className="ghost" data-testid="layer-down" title="Move down" disabled={index === layers.length - 1} onClick={() => move(index, 1)}>▼</button>
                    <button className="ghost" data-testid="layer-style" onClick={() => setExpanded(expanded === layer.id ? null : layer.id)}>style</button>
                    <button className="ghost danger" data-testid="layer-remove" title="Remove layer" onClick={() => removeOne(layer.id)}>×</button>
                  </div>
                </div>
                {expanded === layer.id && (
                  <StyleEditor layer={layer} onChange={(patch) => setLayers((current) => updateLayerStyle(current, layer.id, patch))} />
                )}
              </li>
            ))}
            {layers.length === 0 && <li className="empty">No layers yet — add a dataset or upload a file.</li>}
          </ol>

          <div className="composer-add">
            <select data-testid="composer-dataset" value={selectedDataset} onChange={(event) => setSelectedDataset(event.target.value)}>
              <option value="">{catalogue.length === 0 ? "no datasets in this store" : "add a dataset…"}</option>
              {catalogue.map((dataset) => (
                <option key={dataset.id} value={dataset.id}>{dataset.id}</option>
              ))}
            </select>
            <button data-testid="composer-add" disabled={busy || selectedDataset === ""} onClick={() => void loadDataset(selectedDataset)}>
              Add layer
            </button>
          </div>

          <details className="composer-upload" open>
            <summary>Upload &amp; import a file</summary>
            <label className="field">
              <span>Data file</span>
              <input
                type="file"
                data-testid="composer-file"
                accept=".geojson,.json,.ndjson,.csv,text/csv,application/json"
                onChange={(event) => pickFile(event.target.files?.[0] ?? null)}
              />
            </label>
            <div className="composer-row">
              <label className="field">
                <span>Dataset</span>
                <input data-testid="composer-upload-dataset" value={uploadDataset} onChange={(event) => setUploadDataset(event.target.value)} />
              </label>
              <label className="field">
                <span>Format</span>
                <select data-testid="composer-upload-format" value={format} onChange={(event) => setFormat(event.target.value)}>
                  <option value="geojson">GeoJSON</option>
                  <option value="ndjson">NDJSON</option>
                  <option value="csv">CSV</option>
                </select>
              </label>
            </div>
            <label className="field">
              <span>SRID</span>
              <input data-testid="composer-upload-srid" value={srid} onChange={(event) => setSrid(event.target.value)} />
            </label>
            <div className="form-actions">
              <button data-testid="composer-upload" disabled={busy || store === "demo"} onClick={() => void uploadAndAdd()}>
                Upload &amp; add layer
              </button>
            </div>
            {store === "demo" && <small className="muted">Choose memory or postgis to import a file.</small>}
          </details>

          {error !== null && (
            <div className="outcome fail" role="alert" data-testid="composer-error">
              <div className="outcome-head">Composer error</div>
              <p className="outcome-error">{error}</p>
            </div>
          )}
          {status !== null && (
            <div className="outcome ok" data-testid="composer-status">
              <p>{status}</p>
            </div>
          )}

          <h3>Published services</h3>
          <ul className="capability-list" data-testid="composer-publications">
            {publications.map((publication) => (
              <li key={publication.name} className="capability-row">
                <span className="capability-name">{publication.name}</span>
                <span className="muted small">{publication.kind} · {publication.store} · {publication.layers.length} layer(s)</span>
                <span className="composer-pub-actions">
                  <button className="ghost" data-testid={`composer-load-${publication.name}`} onClick={() => void loadPublication(publication)}>load</button>
                  <button className="ghost danger" onClick={() => void deletePublication(publication)}>delete</button>
                </span>
              </li>
            ))}
            {publications.length === 0 && <li className="empty">No publications.</li>}
          </ul>
        </aside>
      </div>
    </section>
  );
}

/** The per-layer style controls. Only the controls relevant to the geometry family are shown. */
function StyleEditor({ layer, onChange }: { layer: ComposerLayer; onChange: (patch: Partial<LayerStyle>) => void }) {
  const style = layer.style;
  return (
    <div className="style-editor" data-testid="style-editor">
      <label className="field">
        <span>Colour</span>
        <input type="color" data-testid="style-color" value={style.color} onChange={(event) => onChange({ color: event.target.value })} />
      </label>
      <label className="field">
        <span>Opacity {style.opacity.toFixed(2)}</span>
        <input type="range" min="0" max="1" step="0.05" value={style.opacity} onChange={(event) => onChange({ opacity: Number(event.target.value) })} />
      </label>
      {layer.geometry !== "point" && (
        <label className="field">
          <span>Line width {style.lineWidth}</span>
          <input type="range" min="0.5" max="10" step="0.5" value={style.lineWidth} onChange={(event) => onChange({ lineWidth: Number(event.target.value) })} />
        </label>
      )}
      {layer.geometry !== "line" && layer.geometry !== "polygon" && (
        <label className="field">
          <span>Point radius {style.radius}</span>
          <input type="range" min="1" max="20" step="1" value={style.radius} onChange={(event) => onChange({ radius: Number(event.target.value) })} />
        </label>
      )}
    </div>
  );
}

/** The composer's base style: the chosen basemap plus the dark canvas. */
function composerStyle(basemap: Basemap): StyleSpecification {
  return {
    version: 8,
    sources: { ...basemapSource(basemap) },
    layers: [
      { id: "background", type: "background", paint: { "background-color": "#10151c" } },
      ...(basemap === "none"
        ? []
        : [{ id: "basemap", type: "raster" as const, source: "basemap", paint: { "raster-opacity": 0.9 } }]),
    ],
  };
}

/**
 * Rebuilds the composer's GeoJSON sources and style layers. Sources are
 * added once and then updated in place; style layers are removed and re-added
 * in reverse list order so the first layer row paints on top. The applied id
 * refs let a later sync clean up layers/sources whose composer layer is gone
 * (and are cleared when a basemap `setStyle` wipes them).
 */
function applyComposerLayers(
  map: MapLibreMap,
  layers: readonly ComposerLayer[],
  features: Record<string, GeoJSON.FeatureCollection>,
  appliedSpecs: { current: string[] },
  appliedSources: { current: string[] },
): void {
  for (const id of appliedSpecs.current) {
    if (map.getLayer(id) !== undefined) map.removeLayer(id);
  }

  const usedSources = new Set(layers.map((layer) => sourceId(layer.id)));
  for (const id of appliedSources.current) {
    if (!usedSources.has(id) && map.getSource(id) !== undefined) map.removeSource(id);
  }

  for (const layer of layers) {
    const id = sourceId(layer.id);
    const data = features[layer.id] ?? EmptyCollection;
    const existing = map.getSource(id) as GeoJSONSource | undefined;
    if (existing !== undefined) existing.setData(data);
    else map.addSource(id, { type: "geojson", data });
  }

  const nextSpecs: string[] = [];
  for (let index = layers.length - 1; index >= 0; index--) {
    const layer = layers[index]!;
    for (const spec of layerSpecs(layer, sourceId(layer.id))) {
      if (map.getLayer(spec.id) === undefined) map.addLayer(spec);
      nextSpecs.push(spec.id);
    }
  }

  appliedSpecs.current = nextSpecs;
  appliedSources.current = [...usedSources];
}

/** Joins decoded batch pages into one preview collection. */
function mergeBatches(batches: readonly FeatureBatch[]): GeoJSON.FeatureCollection {
  const features: GeoJSON.Feature[] = [];
  for (const batch of batches) features.push(...batchToGeoJson(batch).features);
  return { type: "FeatureCollection", features };
}

/** Fits the camera to a collection when it has coordinates. */
function fitMap(map: MapLibreMap, collection: GeoJSON.FeatureCollection): void {
  const coordinates = collectionCoordinates(collection);
  if (coordinates.length === 0) return;
  const bounds = new LngLatBounds(coordinates[0]!, coordinates[0]!);
  for (const [x, y] of coordinates) bounds.extend([x, y]);
  map.fitBounds(bounds, { padding: 40, maxZoom: 12, duration: 0 });
}

function formatFor(fileName: string): string {
  const lower = fileName.toLowerCase();
  if (lower.endsWith(".csv")) return "csv";
  if (lower.endsWith(".ndjson") || lower.endsWith(".geojsonl")) return "ndjson";
  return "geojson";
}

function datasetFromFileName(fileName: string): string {
  const base = fileName.replace(/\.[^.]+$/, "").toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "");
  return `public.${base === "" ? "upload" : base}`;
}

function messageOf(failure: unknown): string {
  return failure instanceof Error ? failure.message : String(failure);
}
