import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";
import { fromBase64, type CapabilitySummaryDto, type JobResponse, type PluginDto } from "@spatial/client";
import { batchToGeoJson, decodeSgeom } from "./sgeom.ts";
import {
  deleteResult,
  geometryToBase64,
  loadJobs,
  loadResults,
  recordJob,
  saveResult,
  type SavedJob,
  type SavedResult,
} from "./persistence.ts";
import { createClient } from "./api.ts";
import { attachGeometryWire, describe, failure, geometryFromWire, outcomeFromResponse, resourceTokenFrom } from "./state-helpers.ts";
import type { RunOutcome } from "./run-outcome.ts";

/** One finished invocation the Run screen shows (inline or job terminal). */
export type { RunOutcome };

export interface WorkbenchActions {
  refreshAll: () => Promise<void>;
  refreshPlugins: () => Promise<void>;
  refreshCapabilities: () => Promise<void>;
  loadCatalogue: () => Promise<void>;
  loadDataset: (datasetId: string) => Promise<void>;
  selectFeature: (id: string | null) => void;
  runInvocation: (capabilityId: string, args: Record<string, unknown>, permissions: string[]) => Promise<RunOutcome>;
  persistResult: (outcome: RunOutcome, name: string, note: string) => void;
  removeResult: (id: string) => void;
}

interface WorkbenchState {
  client: ReturnType<typeof createClient>;
  plugins: PluginDto[];
  capabilities: CapabilitySummaryDto[];
  catalogueDatasets: string[];
  selectedDataset: string | null;
  geojson: GeoJSON.FeatureCollection;
  resultGeojson: GeoJSON.FeatureCollection;
  selectedFeatureId: string | null;
  selectedProperties: Record<string, unknown> | null;
  /** The wire-encoded <c>{$geometry}</c> tag of the selected feature, for capability arguments. */
  selectedGeometryWire: unknown;
  savedResults: SavedResult[];
  savedJobs: SavedJob[];
  recentOutcome: RunOutcome | null;
  runningJobs: string[];
  error: string | null;
  busy: boolean;
  actions: WorkbenchActions;
}

const Context = createContext<WorkbenchState | null>(null);

export function WorkbenchProvider({ children }: { children: ReactNode }) {
  const client = useMemo(() => createClient(), []);
  const [plugins, setPlugins] = useState<PluginDto[]>([]);
  const [capabilities, setCapabilities] = useState<CapabilitySummaryDto[]>([]);
  const [catalogueDatasets, setCatalogueDatasets] = useState<string[]>([]);
  const [selectedDataset, setSelectedDataset] = useState<string | null>(null);
  const [geojson, setGeojson] = useState<GeoJSON.FeatureCollection>({ type: "FeatureCollection", features: [] });
  const [resultGeojson, setResultGeojson] = useState<GeoJSON.FeatureCollection>({ type: "FeatureCollection", features: [] });
  const [geometryWireById, setGeometryWireById] = useState<Record<string, unknown>>({});
  const [selectedFeatureId, setSelectedFeatureId] = useState<string | null>(null);
  const [selectedProperties, setSelectedProperties] = useState<Record<string, unknown> | null>(null);
  const [savedResults, setSavedResults] = useState<SavedResult[]>(() => loadResults());
  const [savedJobs, setSavedJobs] = useState<SavedJob[]>(() => loadJobs());
  const [recentOutcome, setRecentOutcome] = useState<RunOutcome | null>(null);
  const [runningJobs, setRunningJobs] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const refreshPlugins = useCallback(async () => {
    try {
      setPlugins(await client.getPlugins());
    } catch (err) {
      setError(describe(err));
    }
  }, [client]);

  const refreshCapabilities = useCallback(async () => {
    try {
      setCapabilities(await client.getCapabilities());
    } catch (err) {
      setError(describe(err));
    }
  }, [client]);

  const loadCatalogue = useCallback(async () => {
    try {
      const servesCatalogue = (await client.getCapabilities()).some(
        (capability) => capability.id === "spatial.catalogue.list@1",
      );
      if (!servesCatalogue) {
        setCatalogueDatasets([]);
        return;
      }
      const response = await client.invoke({ capability: "spatial.catalogue.list@1" });
      if (!response.ok) {
        setError(`catalogue: ${response.error?.message ?? "failed"}`);
        return;
      }
      const token = resourceTokenFrom(response);
      if (token === null) return;
      const datasets: string[] = [];
      for await (const item of client.readStream(token)) {
        if (typeof item === "string") {
          const parsed = JSON.parse(item) as { id?: string };
          if (typeof parsed.id === "string") datasets.push(parsed.id);
        }
      }
      setCatalogueDatasets(datasets);
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
        const response = await client.invoke({
          capability: "spatial.feature.scan@1",
          arguments: { dataset: datasetId },
          permissions: ["spatial.feature.read"],
        });
        if (!response.ok) {
          setError(`scan ${datasetId}: ${response.error?.message ?? "failed"}`);
          setGeojson({ type: "FeatureCollection", features: [] });
          return;
        }
        const token = resourceTokenFrom(response);
        if (token === null) return;
        const features: GeoJSON.Feature[] = [];
        const wires: Record<string, unknown> = {};
        for await (const batch of client.readFeatureBatches(token)) {
          features.push(...batchToGeoJson(batch).features);
          for (const feature of batch.features) attachGeometryWire(wires, feature.id, feature);
        }
        setGeojson({ type: "FeatureCollection", features });
        setGeometryWireById(wires);
        setSelectedFeatureId(null);
        setSelectedProperties(null);
      } catch (err) {
        setError(describe(err));
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

  const selectedGeometryWire = selectedFeatureId === null ? null : (geometryWireById[selectedFeatureId] ?? null);

  const runInvocation = useCallback(
    async (capabilityId: string, args: Record<string, unknown>, permissions: string[]): Promise<RunOutcome> => {
      setError(null);
      try {
        const response = await client.invoke({ capability: capabilityId, arguments: args, permissions });
        const outcome = response.kind === "completed"
          ? outcomeFromResponse(response)
          : response.kind === "job" && response.job
            ? await watchJob(response.job.jobId, capabilityId)
            : failure("the host answered with an unexpected invocation kind");
        setRecentOutcome(outcome);
        applyResultToMap(outcome, setResultGeojson);
        return outcome;
      } catch (err) {
        const outcome = failure(describe(err));
        setRecentOutcome(outcome);
        return outcome;
      }
    },
    [client],
  );

  const watchJob = useCallback(async (jobId: string, capability: string): Promise<RunOutcome> => {
    setRunningJobs((jobs) => [...jobs, jobId]);
    try {
      const job = await client.waitForJob(jobId);
      setSavedJobs(recordJob({ id: job.id, capability, state: job.state, startedAt: job.createdAt, provider: job.provider }));
      const result = await readJobResult(client, job);
      const outcome: RunOutcome = {
        capability,
        provider: job.provider,
        ok: job.state === "completed",
        error: job.state === "completed" ? null : `job ended ${job.state}${job.errorCode ? ` (${job.errorCode})` : ""}`,
        result,
        jobId,
        step: null,
        finishedAt: new Date().toISOString(),
      };
      return outcome;
    } catch (err) {
      return failure(describe(err), jobId);
    } finally {
      setRunningJobs((jobs) => jobs.filter((id) => id !== jobId));
    }
  }, [client]);

  const persistResult = useCallback((outcome: RunOutcome, name: string, note: string) => {
    const saved: SavedResult = {
      id: crypto.randomUUID(),
      name,
      capability: outcome.capability,
      provider: outcome.provider,
      savedAt: new Date().toISOString(),
      geometryBase64: geometryToBase64(outcome.result),
      valueJson: scalarToJson(outcome.result),
      note,
    };
    setSavedResults(saveResult(saved));
    if (saved.geometryBase64 !== null) {
      setResultGeojson((current) => ({
        type: "FeatureCollection",
        features: [...current.features, savedResultFeature(saved)],
      }));
    }
  }, []);

  const removeResult = useCallback((id: string) => {
    setSavedResults(deleteResult(id));
  }, []);

  const refreshAll = useCallback(async () => {
    setBusy(true);
    await Promise.all([refreshCapabilities(), refreshPlugins(), loadCatalogue()]);
    setBusy(false);
  }, [refreshCapabilities, refreshPlugins, loadCatalogue]);

  const actions: WorkbenchActions = useMemo(
    () => ({ refreshAll, refreshPlugins, refreshCapabilities, loadCatalogue, loadDataset, selectFeature, runInvocation, persistResult, removeResult }),
    [refreshAll, refreshPlugins, refreshCapabilities, loadCatalogue, loadDataset, selectFeature, runInvocation, persistResult, removeResult],
  );

  const state: WorkbenchState = useMemo(
    () => ({
      client,
      plugins,
      capabilities,
      catalogueDatasets,
      selectedDataset,
      geojson,
      resultGeojson,
      selectedFeatureId,
      selectedProperties,
      selectedGeometryWire,
      savedResults,
      savedJobs,
      recentOutcome,
      runningJobs,
      error,
      busy,
      actions,
    }),
    [client, plugins, capabilities, catalogueDatasets, selectedDataset, geojson, resultGeojson, selectedFeatureId, selectedProperties, selectedGeometryWire, savedResults, savedJobs, recentOutcome, runningJobs, error, busy, actions],
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

async function readJobResult(client: ReturnType<typeof createClient>, job: JobResponse): Promise<unknown> {
  const events = await client.getJobEvents(job.id).catch(() => ({ events: [] as never[] }));
  const resourceEvent = events.events.find((event) => event.resource !== null && event.resource !== undefined);
  if (resourceEvent?.resource?.id) {
    const first: unknown[] = [];
    for await (const item of client.readStream(resourceEvent.resource.id)) {
      if (first.length < 2) first.push(item);
    }
    return first.length > 0 ? { streamPreview: first } : null;
  }
  return null;
}

function applyResultToMap(outcome: RunOutcome, setter: (update: (current: GeoJSON.FeatureCollection) => GeoJSON.FeatureCollection) => void): void {
  const geometry = geometryFromWire(outcome.result);
  if (geometry === null) return;
  setter((current) => ({
    type: "FeatureCollection",
    features: [
      ...current.features,
      {
        type: "Feature",
        id: `result-${Date.now()}`,
        properties: { capability: outcome.capability, provider: outcome.provider, kind: "invocation" },
        geometry: geometry as GeoJSON.Geometry,
      },
    ],
  }));
}

function savedResultFeature(saved: SavedResult): GeoJSON.Feature {
  const fallback = { type: "Feature", id: saved.id, properties: { name: saved.name, capability: saved.capability, provider: saved.provider, kind: "saved" }, geometry: { type: "Point", coordinates: [0, 0] } } as GeoJSON.Feature;
  if (saved.geometryBase64 === null) return fallback;
  try {
    const geometry = decodeSgeom(fromBase64(saved.geometryBase64));
    return {
      type: "Feature",
      id: saved.id,
      properties: { name: saved.name, capability: saved.capability, provider: saved.provider, kind: "saved" },
      geometry: geometry as GeoJSON.Geometry,
    };
  } catch {
    // Unsupported or malformed saved geometry: persist the record, skip the map.
    return fallback;
  }
}

function scalarToJson(value: unknown): string | null {
  if (value === null || value === undefined || typeof value === "object") return null;
  return JSON.stringify(value);
}