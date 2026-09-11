import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from "react";
import { batchToGeoJson, decodeSgeom } from "./sgeom.ts";
import {
  deleteResult,
  loadResults,
  loadRuns,
  recordRun,
  saveResult,
  type SavedResult,
  type SavedRun,
} from "./persistence.ts";
import { createClient } from "./api.ts";
import { attachGeometryBytes, describe, failure, fromBase64, geometryFromBase64, toBase64, withoutOperationResults } from "./state-helpers.ts";
import type { RunOutcome } from "./run-outcome.ts";

/** One finished operation the Run screen shows. */
export type { RunOutcome };

export interface WorkbenchActions {
  refreshAll: () => Promise<void>;
  loadCatalogue: () => Promise<void>;
  loadDataset: (datasetId: string) => Promise<void>;
  selectFeature: (id: string | null) => void;
  runOperation: (op: string, values: Record<string, unknown>) => Promise<RunOutcome>;
  cancelRunning: () => void;
  persistResult: (outcome: RunOutcome, name: string, note: string) => void;
  removeResult: (id: string) => void;
  /** Drops unsaved operation previews from the map, keeping saved results. */
  clearResults: () => void;
}

interface WorkbenchState {
  client: ReturnType<typeof createClient>;
  catalogueDatasets: string[];
  selectedDataset: string | null;
  geojson: GeoJSON.FeatureCollection;
  resultGeojson: GeoJSON.FeatureCollection;
  selectedFeatureId: string | null;
  selectedProperties: Record<string, unknown> | null;
  /** The base64 SGEOM bytes of the selected feature, for operation arguments. */
  selectedGeometryBase64: string | null;
  savedResults: SavedResult[];
  savedRuns: SavedRun[];
  recentOutcome: RunOutcome | null;
  running: boolean;
  error: string | null;
  busy: boolean;
  actions: WorkbenchActions;
}

const Context = createContext<WorkbenchState | null>(null);

export function WorkbenchProvider({ children }: { children: ReactNode }) {
  const client = useMemo(() => createClient(), []);
  const [catalogueDatasets, setCatalogueDatasets] = useState<string[]>([]);
  const [selectedDataset, setSelectedDataset] = useState<string | null>(null);
  const [geojson, setGeojson] = useState<GeoJSON.FeatureCollection>({ type: "FeatureCollection", features: [] });
  const [resultGeojson, setResultGeojson] = useState<GeoJSON.FeatureCollection>({ type: "FeatureCollection", features: [] });
  const [geometryBase64ById, setGeometryBase64ById] = useState<Record<string, string>>({});
  const [selectedFeatureId, setSelectedFeatureId] = useState<string | null>(null);
  const [selectedProperties, setSelectedProperties] = useState<Record<string, unknown> | null>(null);
  const [savedResults, setSavedResults] = useState<SavedResult[]>(() => loadResults());
  const [savedRuns, setSavedRuns] = useState<SavedRun[]>(() => loadRuns());
  const [recentOutcome, setRecentOutcome] = useState<RunOutcome | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  const loadCatalogue = useCallback(async () => {
    try {
      const catalogue = await client.catalogue();
      setCatalogueDatasets(catalogue.datasets.map((dataset) => dataset.id));
    } catch (err) {
      setError(describe(err));
    }
  }, [client]);

  const loadDataset = useCallback(
    async (datasetId: string) => {
      setBusy(true);
      setError(null);
      try {
        setSelectedDataset(datasetId);
        const batches = await client.scan(datasetId);
        const features: GeoJSON.Feature[] = [];
        const bytes: Record<string, string> = {};
        for (const batch of batches) {
          features.push(...batchToGeoJson(batch).features);
          for (const feature of batch.features) attachGeometryBytes(bytes, feature.id, feature);
        }
        setGeojson({ type: "FeatureCollection", features });
        setGeometryBase64ById(bytes);
        setSelectedFeatureId(null);
        setSelectedProperties(null);
      } catch (err) {
        setError(describe(err));
        setGeojson({ type: "FeatureCollection", features: [] });
      } finally {
        setBusy(false);
      }
    },
    [client],
  );

  const selectFeature = useCallback(
    (id: string | null) => {
      setSelectedFeatureId(id);
      if (id === null) {
        setSelectedProperties(null);
        return;
      }
      const feature = geojson.features.find((candidate) => String(candidate.id) === id);
      setSelectedProperties(feature ? (feature.properties as Record<string, unknown>) : null);
    },
    [geojson.features],
  );

  const selectedGeometryBase64 = selectedFeatureId === null ? null : (geometryBase64ById[selectedFeatureId] ?? null);

  const runOperation = useCallback(
    async (op: string, values: Record<string, unknown>): Promise<RunOutcome> => {
      setError(null);
      setRunning(true);
      const abort = new AbortController();
      abortRef.current = abort;
      const startedAt = new Date().toISOString();
      try {
        const outcome = await execute(client, op, values, selectedGeometryBase64, abort.signal);
        setRecentOutcome(outcome);
        setSavedRuns(recordRun({ id: crypto.randomUUID(), op, ok: outcome.ok, startedAt, detail: outcome.summary ?? outcome.error }));
        applyResultToMap(outcome, setResultGeojson);
        return outcome;
      } catch (err) {
        if (abort.signal.aborted) {
          const outcome: RunOutcome = { op, ok: false, error: "cancelled", geometryBase64: null, summary: null, finishedAt: new Date().toISOString() };
          setRecentOutcome(outcome);
          return outcome;
        }
        const outcome = failure(op, describe(err));
        setRecentOutcome(outcome);
        return outcome;
      } finally {
        setRunning(false);
        abortRef.current = null;
      }
    },
    [client, selectedGeometryBase64],
  );

  const cancelRunning = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  const persistResult = useCallback((outcome: RunOutcome, name: string, note: string) => {
    const saved: SavedResult = {
      id: crypto.randomUUID(),
      name,
      op: outcome.op,
      savedAt: new Date().toISOString(),
      geometryBase64: outcome.geometryBase64,
      summary: outcome.summary,
      note,
    };
    setSavedResults(saveResult(saved));
    if (saved.geometryBase64 !== null) {
      const geometry = geometryFromBase64(saved.geometryBase64);
      if (geometry !== null) {
        const feature: GeoJSON.Feature = {
          type: "Feature",
          id: saved.id,
          properties: { name: saved.name, op: saved.op, kind: "saved" },
          geometry: geometry as GeoJSON.Geometry,
        };
        setResultGeojson((current) => ({ type: "FeatureCollection", features: [...current.features, feature] }));
      }
    }
  }, []);

  const removeResult = useCallback((id: string) => {
    setSavedResults(deleteResult(id));
  }, []);

  const clearResults = useCallback(() => {
    setResultGeojson(withoutOperationResults);
  }, []);

  const refreshAll = useCallback(async () => {
    setBusy(true);
    await loadCatalogue();
    setBusy(false);
  }, [loadCatalogue]);

  const actions: WorkbenchActions = useMemo(
    () => ({ refreshAll, loadCatalogue, loadDataset, selectFeature, runOperation, cancelRunning, persistResult, removeResult, clearResults }),
    [refreshAll, loadCatalogue, loadDataset, selectFeature, runOperation, cancelRunning, persistResult, removeResult, clearResults],
  );

  const state: WorkbenchState = useMemo(
    () => ({
      client,
      catalogueDatasets,
      selectedDataset,
      geojson,
      resultGeojson,
      selectedFeatureId,
      selectedProperties,
      selectedGeometryBase64,
      savedResults,
      savedRuns,
      recentOutcome,
      running,
      error,
      busy,
      actions,
    }),
    [client, catalogueDatasets, selectedDataset, geojson, resultGeojson, selectedFeatureId, selectedProperties, selectedGeometryBase64, savedResults, savedRuns, recentOutcome, running, error, busy, actions],
  );

  return <Context.Provider value={state}>{children}</Context.Provider>;
}

export function useWorkbench(): WorkbenchState {
  const state = useContext(Context);
  if (state === null) {
    throw new Error("useWorkbench must be used inside WorkbenchProvider");
  }
  return state;
}

type Client = ReturnType<typeof createClient>;

async function execute(
  client: Client,
  op: string,
  values: Record<string, unknown>,
  selectedBase64: string | null,
  signal: AbortSignal,
): Promise<RunOutcome> {
  const finishedAt = () => new Date().toISOString();
  switch (op) {
    case "buffer": {
      const geometry = requireSelection(selectedBase64);
      const result = await client.buffer(fromBase64(geometry), Number(values.distance ?? 1), intOr(values.quadrantSegments, 8), signal);
      return { op, ok: true, error: null, geometryBase64: toBase64(result), summary: `${result.length} bytes buffered`, finishedAt: finishedAt() };
    }
    case "intersection": {
      const geometry = requireSelection(selectedBase64);
      const other = requireText(values.other, "other geometry");
      const result = await client.intersection(fromBase64(geometry), fromBase64(other), signal);
      return { op, ok: true, error: null, geometryBase64: toBase64(result), summary: `${result.length} bytes intersected`, finishedAt: finishedAt() };
    }
    case "validate": {
      const geometry = requireSelection(selectedBase64);
      const valid = await client.validate(fromBase64(geometry), signal);
      return { op, ok: true, error: null, geometryBase64: null, summary: valid ? "valid" : "invalid", finishedAt: finishedAt() };
    }
    case "simplify": {
      const geometry = requireSelection(selectedBase64);
      const result = await client.simplify(fromBase64(geometry), Number(values.tolerance ?? 0.5), signal);
      return { op, ok: true, error: null, geometryBase64: toBase64(result), summary: `${result.length} bytes simplified`, finishedAt: finishedAt() };
    }
    case "transform": {
      const geometry = requireSelection(selectedBase64);
      const target = requireText(values.target, "target CRS");
      const source = typeof values.source === "string" && values.source.trim() !== "" ? values.source.trim() : undefined;
      const result = await client.transform(fromBase64(geometry), target, source, signal);
      return { op, ok: true, error: null, geometryBase64: toBase64(result), summary: `transformed to ${target}`, finishedAt: finishedAt() };
    }
    case "describe": {
      const description = await client.describeCrs(requireText(values.crs, "CRS"), signal);
      return { op, ok: true, error: null, geometryBase64: null, summary: `${description.authority}:${description.code} ${description.name}`, finishedAt: finishedAt() };
    }
    case "scan": {
      const batches = await client.scan(requireText(values.dataset, "dataset"), "demo", signal);
      const count = batches.reduce((sum, batch) => sum + batch.features.length, 0);
      return { op, ok: true, error: null, geometryBase64: null, summary: `${count} features in ${batches.length} batches`, finishedAt: finishedAt() };
    }
    case "query": {
      const bbox = readBbox(values);
      const filter = typeof values.filter === "string" && values.filter.trim() !== "" ? values.filter.trim() : undefined;
      const batches = await client.query(requireText(values.dataset, "dataset"), { bbox: bbox ?? undefined, filter }, "demo", signal);
      const count = batches.reduce((sum, batch) => sum + batch.features.length, 0);
      return { op, ok: true, error: null, geometryBase64: null, summary: `${count} features match`, finishedAt: finishedAt() };
    }
    case "sleep": {
      const slept = await client.sleep(intOr(values.milliseconds, 1000), signal);
      return { op, ok: true, error: null, geometryBase64: null, summary: `slept ${slept} ms`, finishedAt: finishedAt() };
    }
    default:
      return failure(op, `unknown operation ${op}`);
  }
}

function requireSelection(selectedBase64: string | null): string {
  if (selectedBase64 === null) throw new Error("no geometry selected — pick a feature on the map");
  return selectedBase64;
}

function requireText(value: unknown, name: string): string {
  if (typeof value !== "string" || value.trim() === "") throw new Error(`${name} is required`);
  return value.trim();
}

function intOr(value: unknown, fallback: number): number {
  if (value === undefined || value === null || value === "") return fallback;
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) throw new Error(`expected a number, got ${String(value)}`);
  return Math.trunc(numeric);
}

function readBbox(values: Record<string, unknown>): { minX: number; minY: number; maxX: number; maxY: number } | null {
  const names = ["minx", "miny", "maxx", "maxy"] as const;
  const present = names.filter((name) => values[name] !== undefined && values[name] !== null && values[name] !== "");
  if (present.length === 0) return null;
  if (present.length !== 4) throw new Error("the bounding box is all-or-none (minx, miny, maxx, maxy)");
  return { minX: Number(values.minx), minY: Number(values.miny), maxX: Number(values.maxx), maxY: Number(values.maxy) };
}

function applyResultToMap(outcome: RunOutcome, setter: (update: (current: GeoJSON.FeatureCollection) => GeoJSON.FeatureCollection) => void): void {
  const geometry = geometryFromBase64(outcome.geometryBase64);
  if (geometry === null) return;
  setter((current) => ({
    type: "FeatureCollection",
    features: [
      ...current.features,
      {
        type: "Feature",
        id: `result-${Date.now()}`,
        properties: { op: outcome.op, kind: "operation" },
        geometry: geometry as GeoJSON.Geometry,
      },
    ],
  }));
}

export function savedResultFeature(saved: SavedResult): GeoJSON.Feature {
  const fallback = { type: "Feature", id: saved.id, properties: { name: saved.name, op: saved.op, kind: "saved" }, geometry: { type: "Point", coordinates: [0, 0] } } as GeoJSON.Feature;
  if (saved.geometryBase64 === null) return fallback;
  const geometry = geometryFromBase64(saved.geometryBase64);
  if (geometry === null) return fallback;
  return {
    type: "Feature",
    id: saved.id,
    properties: { name: saved.name, op: saved.op, kind: "saved" },
    geometry: geometry as GeoJSON.Geometry,
  };
}
