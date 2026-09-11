import { useState } from "react";
import { useWorkbench, type RunOutcome } from "../state.tsx";

/**
 * The service catalogue (ADR-0033): the fixed set of typed operations the
 * host serves plus the dataset catalogue from the demo store.
 */
export function OverviewScreen() {
  const { catalogueDatasets, actions } = useWorkbench();
  const [showDetail, setShowDetail] = useState<string | null>(null);

  return (
    <section className="screen overview-screen">
      <h2>Service catalogue</h2>
      <p className="muted">
        The host serves a fixed set of typed operations over in-process spatial services —
        geometry operations, coordinate transforms, dataset catalogue and feature access.
      </p>

      <h3>Operations</h3>
      <ul className="capability-list">
        {ServiceList.map((service) => (
          <li key={service.route}>
            <button className="capability-row" onClick={() => setShowDetail(showDetail === service.route ? null : service.route)}>
              <code>{service.route}</code>
              <span className="muted">{service.purpose}</span>
            </button>
            {showDetail === service.route && (
              <div className="capability-detail">
                <div><strong>Service:</strong> <code>{service.service}</code></div>
                <div><strong>Input:</strong> <code>{service.input}</code></div>
                <div><strong>Output:</strong> <code>{service.output}</code></div>
              </div>
            )}
          </li>
        ))}
      </ul>

      <h3>Catalogue datasets</h3>
      {catalogueDatasets.length === 0 ? (
        <p className="empty">
          No datasets are listed. The demo store is always available — <button className="ghost" onClick={() => void actions.loadCatalogue()}>re-load catalogue</button>
        </p>
      ) : (
        <ul className="tag-list">
          {catalogueDatasets.map((dataset) => (
            <li key={dataset}><button className="tag" onClick={() => void actions.loadDataset(dataset)}>{dataset}</button></li>
          ))}
        </ul>
      )}
    </section>
  );
}

const ServiceList = [
  { route: "POST /api/geometry/buffer", service: "geometry operations", purpose: "Expands or shrinks a geometry by a distance.", input: "base64 SGEOM + distance + quadrantSegments", output: "base64 SGEOM" },
  { route: "POST /api/geometry/intersection", service: "geometry operations", purpose: "Returns the overlap of two geometries.", input: "two base64 SGEOM geometries", output: "base64 SGEOM" },
  { route: "POST /api/geometry/validate", service: "geometry operations", purpose: "Reports OGC validity (invalid is a successful false).", input: "base64 SGEOM", output: "{valid}" },
  { route: "POST /api/geometry/simplify", service: "geometry operations", purpose: "Douglas-Peucker simplification.", input: "base64 SGEOM + tolerance", output: "base64 SGEOM" },
  { route: "POST /api/crs/describe", service: "CRS directory", purpose: "Describes one CRS identity.", input: "{crs}", output: "CrsDescription" },
  { route: "POST /api/coordinates/transform", service: "coordinate transforms", purpose: "Transforms a geometry to the target CRS.", input: "base64 SGEOM + source? + target", output: "base64 SGEOM" },
  { route: "GET /api/catalogue", service: "demo / postgis stores", purpose: "Lists dataset summaries.", input: "?store & ?pattern", output: "DatasetSummary[]" },
  { route: "POST /api/features/scan", service: "demo / postgis stores", purpose: "Reads every feature as canonical batches.", input: "{dataset}", output: "base64 SFBAT[]" },
  { route: "POST /api/features/query", service: "demo / postgis stores", purpose: "Filters by bbox and/or attribute expression.", input: "{dataset, bbox?, filter?}", output: "base64 SFBAT[]" },
  { route: "POST /api/demo/sleep", service: "demo jobs", purpose: "A cancellable host-side delay.", input: "{milliseconds}", output: "{slept}" },
];

/** Renders the outcome of one operation (used by Run and Runtime screens). */
export function OutcomeView({ outcome }: { outcome: RunOutcome }) {
  return (
    <div className={`outcome ${outcome.ok ? "ok" : "fail"}`} data-testid="outcome">
      <div className="outcome-head">
        <span className={`state ${outcome.ok ? "completed" : "failed"}`}>{outcome.ok ? "completed" : "failed"}</span>
        <code>{outcome.op || "(operation)"}</code>
      </div>
      {outcome.error && <div className="outcome-error" role="alert">{outcome.error}</div>}
      {outcome.summary && <div className="outcome-summary" data-testid="result-summary">{outcome.summary}</div>}
      {outcome.geometryBase64 && (
        <pre className="result-json" data-testid="result-json">{`SGEOM ${outcome.geometryBase64.length} base64 chars`}</pre>
      )}
    </div>
  );
}
