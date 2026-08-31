import { useWorkbench, type RunOutcome } from "../state.tsx";
import { providerStateLabel } from "../forms.ts";
import { useState } from "react";

/**
 * The provider/catalogue browser (Phase 10): every serving provider and the
 * capability contracts they serve, grouped for inspection — the "provider or
 * operation provenance" surface of plan §17.12.
 */
export function OverviewScreen() {
  const { plugins, capabilities, catalogueDatasets, actions } = useWorkbench();
  const [showDetail, setShowDetail] = useState<string | null>(null);

  return (
    <section className="screen overview-screen">
      <h2>Provider &amp; capability catalogue</h2>
      <p className="muted">
        Discover the capabilities of the connected host and the providers that serve them. Spatial
        operations run as replaceable out-of-process plugins.
      </p>

      <h3>Providers</h3>
      {plugins.length === 0 ? (
        <p className="empty">No plugin packages are loaded (Spatial:PackagesRoot empty).</p>
      ) : (
        <table className="grid">
          <thead>
            <tr>
              <th>Provider</th>
              <th>Runtime</th>
              <th>State</th>
              <th>PID</th>
              <th>Restarts</th>
              <th>Capabilities</th>
            </tr>
          </thead>
          <tbody>
            {plugins.map((plugin) => (
              <tr key={plugin.id}>
                <td><code>{plugin.id}</code></td>
                <td>{plugin.runtime}</td>
                <td><span className={`state ${plugin.state.toLowerCase()}`}>{providerStateLabel(plugin.state)}</span></td>
                <td>{plugin.processId ?? "—"}</td>
                <td>{String(plugin.restartCount)}</td>
                <td>{plugin.capabilities.length}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h3>Capabilities</h3>
      {capabilities.length === 0 ? (
        <p className="empty">None registered — start the host with plugin packages.</p>
      ) : (
        <ul className="capability-list">
          {capabilities.map((capability) => (
            <li key={capability.id}>
              <button className="capability-row" onClick={() => setShowDetail(showDetail === capability.id ? null : capability.id)}>
                <code>{capability.id}</code>
                <span className="muted">{capability.purpose}</span>
                <span className="providers">{capability.providers.join(", ")}</span>
              </button>
              {showDetail === capability.id && (
                <div className="capability-detail">
                  <div><strong>Input:</strong> <code>{capability.inputSchema}</code></div>
                  <div><strong>Output:</strong> <code>{capability.outputSchema}</code></div>
                  <div><strong>Traits:</strong> {capability.traits.join(", ") || "none"}</div>
                  <div><strong>Permissions:</strong> {capability.requiredPermissions.join(", ") || "none"}</div>
                  <div><strong>Providers:</strong> {capability.providers.join(", ") || "none"}</div>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}

      <h3>Catalogue datasets</h3>
      {catalogueDatasets.length === 0 ? (
        <p className="empty">
          No datasource provider is connected, so no datasets are listed. Start the host with a
          catalogue provider (postgis@1 or demo@1) to browse datasets. <button className="ghost" onClick={() => void actions.loadCatalogue()}>re-load catalogue</button>
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

/** Renders the outcome of one invocation (used by Run and Runtime screens). */
export function OutcomeView({ outcome }: { outcome: RunOutcome }) {
  return (
    <div className={`outcome ${outcome.ok ? "ok" : "fail"}`} data-testid="outcome">
      <div className="outcome-head">
        <span className={`state ${outcome.ok ? "completed" : "failed"}`}>{outcome.ok ? "completed" : "failed"}</span>
        <code>{outcome.capability || "(invocation)"}</code>
        {outcome.provider && <span className="muted">served by {outcome.provider}{outcome.step ? ` · ${outcome.step}` : ""}</span>}
        {outcome.jobId && <span className="muted">· job {outcome.jobId}</span>}
      </div>
      {outcome.error && <div className="outcome-error" role="alert">{outcome.error}</div>}
      {outcome.result !== null && (
        <pre className="result-json" data-testid="result-json">{JSON.stringify(outcome.result, jsonReplacer, 2)}</pre>
      )}
    </div>
  );
}

/** bigint and Uint8Array friendly JSON print for previews. */
function jsonReplacer(_key: string, value: unknown): unknown {
  if (typeof value === "bigint") return value.toString();
  if (value instanceof Uint8Array) return { $bytes: `[${value.length} bytes]` };
  return value;
}