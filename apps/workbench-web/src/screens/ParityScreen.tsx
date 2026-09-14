import { useEffect, useRef, useState } from "react";
import { hostBaseUrl } from "../api.ts";
import {
  ParityDefaults,
  buildFeatureCountUrl,
  buildFeatureQueryUrl,
  buildMapExportUrl,
  localServiceRoots,
  parseBbox,
  parseSize,
  type ParityBbox,
  type ParitySize,
} from "../parity.ts";

type ParityTab = "map" | "feature";

interface AppliedSettings {
  bbox: ParityBbox;
  bboxText: string;
  sr: number;
  size: ParitySize;
  sizeText: string;
  localMap: string;
  esriMap: string;
  localFeature: string;
  esriFeature: string;
  where: string;
  maxRecords: number;
}

interface FeaturePanelState {
  status: "idle" | "loading" | "ready" | "error";
  count: number | null;
  exceededTransferLimit: boolean;
  rows: Record<string, unknown>[];
  columns: string[];
  error: string | null;
}

const idlePanel: FeaturePanelState = {
  status: "idle",
  count: null,
  exceededTransferLimit: false,
  rows: [],
  columns: [],
  error: null,
};

/**
 * Visual parity harness (T-072): side-by-side public Esri vs localhost
 * panels for the same bbox. The Map tab compares MapServer/export images
 * through plain `<img>` tags (`f=image` needs no CORS fetch); the Feature
 * tab compares FeatureServer/query counts and sample attributes fetched as
 * JSON. The localhost side uses only the public GeoServices surface; the
 * screen holds no spatial logic — URL strings in, pixels and JSON out.
 */
export function ParityScreen() {
  const [tab, setTab] = useState<ParityTab>("map");
  const [bboxText, setBboxText] = useState<string>(ParityDefaults.bbox);
  const [srText, setSrText] = useState<string>(String(ParityDefaults.sr));
  const [sizeText, setSizeText] = useState<string>(ParityDefaults.size);
  const [localMap, setLocalMap] = useState<string>(ParityDefaults.localMap);
  const [esriMap, setEsriMap] = useState<string>(ParityDefaults.esriMap);
  const [localFeature, setLocalFeature] = useState<string>(ParityDefaults.localFeature);
  const [esriFeature, setEsriFeature] = useState<string>(ParityDefaults.esriFeature);
  const [where, setWhere] = useState<string>(ParityDefaults.where);
  const [maxRecordsText, setMaxRecordsText] = useState(String(ParityDefaults.maxRecords));
  const [formError, setFormError] = useState<string | null>(null);
  const [applied, setApplied] = useState<AppliedSettings | null>(() => defaultApplied());

  const apply = () => {
    const bbox = parseBbox(bboxText);
    if (bbox === null) {
      setFormError("the bbox is four numbers: minX,miny,maxX,maxY with min < max");
      return;
    }
    const size = parseSize(sizeText);
    if (size === null) {
      setFormError("the image size is two positive integers: width,height");
      return;
    }
    const sr = Number(srText);
    if (!Number.isInteger(sr)) {
      setFormError("the spatial reference is a WKID integer (e.g. 4326)");
      return;
    }
    const maxRecords = Number(maxRecordsText);
    if (!Number.isInteger(maxRecords) || maxRecords <= 0) {
      setFormError("max records is a positive integer");
      return;
    }
    if (localMap.trim() === "" || esriMap.trim() === "" || localFeature.trim() === "" || esriFeature.trim() === "") {
      setFormError("the service names and Esri roots must not be empty");
      return;
    }
    setFormError(null);
    setApplied({
      bbox,
      bboxText: bboxText.trim(),
      sr,
      size,
      sizeText: sizeText.trim(),
      localMap: localMap.trim(),
      esriMap: esriMap.trim(),
      localFeature: localFeature.trim(),
      esriFeature: esriFeature.trim(),
      where: where.trim() === "" ? "1=1" : where.trim(),
      maxRecords,
    });
  };

  return (
    <section className="screen parity-screen">
      <h2>Parity</h2>
      <p className="muted">
        Side-by-side public Esri vs localhost panels for the same bbox, over the T-066A seed services.
        The localhost side talks only to the public GeoServices surface; nothing here renders or
        queries spatially — URLs in, pixels and JSON out.
      </p>

      <div className="parity-controls">
        <div className="field">
          <label htmlFor="parity-bbox">Bbox (minX,miny,maxX,maxY)</label>
          <input id="parity-bbox" data-testid="parity-bbox" value={bboxText} onChange={(event) => setBboxText(event.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="parity-sr">WKID</label>
          <input id="parity-sr" data-testid="parity-sr" value={srText} onChange={(event) => setSrText(event.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="parity-size">Size (w,h)</label>
          <input id="parity-size" data-testid="parity-size" value={sizeText} onChange={(event) => setSizeText(event.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="parity-where">Where</label>
          <input id="parity-where" data-testid="parity-where" value={where} onChange={(event) => setWhere(event.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="parity-max-records">Max records</label>
          <input id="parity-max-records" data-testid="parity-max-records" value={maxRecordsText} onChange={(event) => setMaxRecordsText(event.target.value)} />
        </div>
        <div className="field parity-apply-field">
          <span aria-hidden="true" />
          <button className="primary" data-testid="parity-apply" onClick={apply}>apply</button>
        </div>
      </div>
      {formError !== null && <p className="parity-error" data-testid="parity-error" role="alert">{formError}</p>}

      <div className="tabs parity-tabs" role="tablist" aria-label="Parity views">
        <button role="tab" aria-selected={tab === "map"} className={`tab${tab === "map" ? " active" : ""}`}
          data-testid="parity-tab-map" onClick={() => setTab("map")}>Map</button>
        <button role="tab" aria-selected={tab === "feature"} className={`tab${tab === "feature" ? " active" : ""}`}
          data-testid="parity-tab-feature" onClick={() => setTab("feature")}>Feature</button>
      </div>

      {applied !== null && tab === "map" && (
        <MapPanels
          bbox={applied.bbox}
          sr={applied.sr}
          size={applied.size}
          localMap={applied.localMap}
          esriMap={applied.esriMap}
          onLocalMapChange={setLocalMap}
          onEsriMapChange={setEsriMap}
          localMapValue={localMap}
          esriMapValue={esriMap}
        />
      )}
      {applied !== null && tab === "feature" && (
        <FeaturePanels
          bbox={applied.bbox}
          sr={applied.sr}
          where={applied.where}
          maxRecords={applied.maxRecords}
          localFeature={applied.localFeature}
          esriFeature={applied.esriFeature}
          onLocalFeatureChange={setLocalFeature}
          onEsriFeatureChange={setEsriFeature}
          localFeatureValue={localFeature}
          esriFeatureValue={esriFeature}
        />
      )}
    </section>
  );
}

function defaultApplied(): AppliedSettings {
  return {
    bbox: parseBbox(ParityDefaults.bbox)!,
    bboxText: ParityDefaults.bbox,
    sr: ParityDefaults.sr,
    size: parseSize(ParityDefaults.size)!,
    sizeText: ParityDefaults.size,
    localMap: ParityDefaults.localMap,
    esriMap: ParityDefaults.esriMap,
    localFeature: ParityDefaults.localFeature,
    esriFeature: ParityDefaults.esriFeature,
    where: ParityDefaults.where,
    maxRecords: ParityDefaults.maxRecords,
  };
}

function MapPanels(props: {
  bbox: ParityBbox;
  sr: number;
  size: ParitySize;
  localMap: string;
  esriMap: string;
  localMapValue: string;
  esriMapValue: string;
  onLocalMapChange: (value: string) => void;
  onEsriMapChange: (value: string) => void;
}) {
  const localRoots = localServiceRoots(hostBaseUrl(), props.localMap);
  const esriUrl = buildMapExportUrl(props.esriMap, props.bbox, props.sr, props.size);
  const localUrl = buildMapExportUrl(localRoots.map, props.bbox, props.sr, props.size);
  return (
    <div>
      <div className="parity-controls">
        <div className="field">
          <label htmlFor="parity-local-map">Localhost map service</label>
          <input id="parity-local-map" data-testid="parity-local-map" value={props.localMapValue} onChange={(event) => props.onLocalMapChange(event.target.value)} />
        </div>
        <div className="field parity-wide">
          <label htmlFor="parity-esri-map">Public Esri MapServer root</label>
          <input id="parity-esri-map" data-testid="parity-esri-map" value={props.esriMapValue} onChange={(event) => props.onEsriMapChange(event.target.value)} />
        </div>
      </div>
      <div className="parity-panels">
        <ExportPanel title="Public Esri" testId="esri" url={esriUrl} />
        <ExportPanel title="Localhost" testId="local" url={localUrl} />
      </div>
    </div>
  );
}

function ExportPanel({ title, testId, url }: { title: string; testId: string; url: string }) {
  const [state, setState] = useState<"loading" | "ready" | "error">("loading");
  // The URL is the identity: a new bbox/service restarts the load state.
  useEffect(() => setState("loading"), [url]);
  return (
    <figure className="parity-panel" data-testid={`parity-${testId}-panel`}>
      <figcaption>
        <b>{title}</b>{" "}
        <span className="muted small" data-testid={`parity-${testId}-status`}>
          {state === "loading" ? "loading…" : state === "ready" ? "rendered" : "failed to load"}
        </span>
      </figcaption>
      <img
        data-testid={`parity-${testId}-image`}
        src={url}
        alt={`${title} MapServer export`}
        onLoad={() => setState("ready")}
        onError={() => setState("error")}
      />
      <figcaption className="small">
        <a className="muted endpoint-url" href={url} target="_blank" rel="noreferrer" data-testid={`parity-${testId}-url`}>
          {url}
        </a>
        {state === "error" && (
          <span className="parity-error" data-testid={`parity-${testId}-error`}>
            {" "}the export did not load — the service may be unreachable or the bbox outside its extent.
          </span>
        )}
      </figcaption>
    </figure>
  );
}

function FeaturePanels(props: {
  bbox: ParityBbox;
  sr: number;
  where: string;
  maxRecords: number;
  localFeature: string;
  esriFeature: string;
  localFeatureValue: string;
  esriFeatureValue: string;
  onLocalFeatureChange: (value: string) => void;
  onEsriFeatureChange: (value: string) => void;
}) {
  const localRoots = localServiceRoots(hostBaseUrl(), props.localFeature);
  const query = { where: props.where, bbox: props.bbox, inSr: props.sr, maxRecords: props.maxRecords };
  const countQuery = { where: props.where, bbox: props.bbox, inSr: props.sr };
  const esriCountUrl = buildFeatureCountUrl(props.esriFeature, ParityDefaults.esriLayer, countQuery);
  const esriSampleUrl = buildFeatureQueryUrl(props.esriFeature, ParityDefaults.esriLayer, query);
  const localCountUrl = buildFeatureCountUrl(localRoots.feature, ParityDefaults.localLayer, countQuery);
  const localSampleUrl = buildFeatureQueryUrl(localRoots.feature, ParityDefaults.localLayer, query);

  const [esri, setEsri] = useState<FeaturePanelState>(idlePanel);
  const [local, setLocal] = useState<FeaturePanelState>(idlePanel);
  const abortRef = useRef<AbortController | null>(null);

  // One cancellable round per applied settings: counts and samples for both
  // sides load in parallel; unmount or a new apply aborts the in-flight
  // round so a slow public host never overwrites fresher results.
  useEffect(() => {
    abortRef.current?.abort();
    const abort = new AbortController();
    abortRef.current = abort;
    setEsri((current) => ({ ...current, status: "loading", error: null }));
    setLocal((current) => ({ ...current, status: "loading", error: null }));
    void loadSide(esriCountUrl, esriSampleUrl, abort.signal).then((state) => {
      if (!abort.signal.aborted) setEsri(state);
    });
    void loadSide(localCountUrl, localSampleUrl, abort.signal).then((state) => {
      if (!abort.signal.aborted) setLocal(state);
    });
    return () => abort.abort();
    // The URLs already encode every applied setting; depending on them
    // keeps the effect honest without re-running on unrelated renders.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [esriCountUrl, esriSampleUrl, localCountUrl, localSampleUrl]);

  useEffect(() => () => abortRef.current?.abort(), []);

  return (
    <div>
      <div className="parity-controls">
        <div className="field">
          <label htmlFor="parity-local-feature">Localhost feature service</label>
          <input id="parity-local-feature" data-testid="parity-local-feature" value={props.localFeatureValue} onChange={(event) => props.onLocalFeatureChange(event.target.value)} />
        </div>
        <div className="field parity-wide">
          <label htmlFor="parity-esri-feature">Public Esri FeatureServer root</label>
          <input id="parity-esri-feature" data-testid="parity-esri-feature" value={props.esriFeatureValue} onChange={(event) => props.onEsriFeatureChange(event.target.value)} />
        </div>
      </div>
      <div className="parity-panels">
        <FeaturePanel title="Public Esri" testId="esri" state={esri} countUrl={esriCountUrl} sampleUrl={esriSampleUrl} />
        <FeaturePanel title="Localhost" testId="local" state={local} countUrl={localCountUrl} sampleUrl={localSampleUrl} />
      </div>
    </div>
  );
}

async function loadSide(countUrl: string, sampleUrl: string, signal: AbortSignal): Promise<FeaturePanelState> {
  try {
    const [countResponse, sampleResponse] = await Promise.all([fetch(countUrl, { signal }), fetch(sampleUrl, { signal })]);
    if (!countResponse.ok) throw new Error(`count query answered ${countResponse.status}`);
    if (!sampleResponse.ok) throw new Error(`sample query answered ${sampleResponse.status}`);
    const countBody = (await countResponse.json()) as { count?: number; error?: { message?: string } };
    if (typeof countBody.count !== "number") {
      throw new Error(typeof countBody.error?.message === "string" ? countBody.error.message : "the count query answered without a count");
    }
    const sampleBody = (await sampleResponse.json()) as {
      features?: { attributes?: Record<string, unknown> }[];
      exceededTransferLimit?: boolean;
      error?: { message?: string };
    };
    if (!Array.isArray(sampleBody.features)) {
      throw new Error(typeof sampleBody.error?.message === "string" ? sampleBody.error.message : "the sample query answered without features");
    }
    const rows = sampleBody.features.map((feature) => feature.attributes ?? {});
    const columns = rows.length === 0 ? [] : Object.keys(rows[0]!);
    return {
      status: "ready",
      count: countBody.count,
      exceededTransferLimit: sampleBody.exceededTransferLimit === true,
      rows,
      columns,
      error: null,
    };
  } catch (err) {
    if (err instanceof DOMException && err.name === "AbortError") {
      return { ...idlePanel, status: "loading" };
    }
    return { ...idlePanel, status: "error", error: err instanceof Error ? err.message : String(err) };
  }
}

function FeaturePanel(props: {
  title: string;
  testId: string;
  state: FeaturePanelState;
  countUrl: string;
  sampleUrl: string;
}) {
  const { state } = props;
  return (
    <figure className="parity-panel" data-testid={`parity-${props.testId}-panel`}>
      <figcaption>
        <b>{props.title}</b>{" "}
        <span className="muted small" data-testid={`parity-${props.testId}-status`}>
          {state.status === "loading" || state.status === "idle"
            ? "loading…"
            : state.status === "ready"
              ? `${state.count} feature${state.count === 1 ? "" : "s"}${state.exceededTransferLimit ? " (more available)" : ""}`
              : "failed to load"}
        </span>
      </figcaption>
      {state.status === "error" && (
        <p className="parity-error" data-testid={`parity-${props.testId}-error`} role="alert">{state.error}</p>
      )}
      {state.status === "ready" && state.rows.length === 0 && (
        <p className="muted" data-testid={`parity-${props.testId}-empty`}>No features in this bbox — widen it or relax the where clause.</p>
      )}
      {state.status === "ready" && state.rows.length > 0 && (
        <div className="parity-table-wrap">
          <table className="grid" data-testid={`parity-${props.testId}-table`}>
            <thead>
              <tr>
                {state.columns.map((column) => (
                  <th key={column}>{column}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {state.rows.map((row, index) => (
                <tr key={index}>
                  {state.columns.map((column) => (
                    <td key={column}>{cellDisplay(row[column])}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <figcaption className="small">
        <a className="muted endpoint-url" href={props.countUrl} target="_blank" rel="noreferrer" data-testid={`parity-${props.testId}-url`}>
          {props.countUrl}
        </a>
      </figcaption>
    </figure>
  );
}

function cellDisplay(value: unknown): string {
  if (value === null || value === undefined) return "∅";
  if (typeof value === "object") return JSON.stringify(value);
  return String(value);
}
