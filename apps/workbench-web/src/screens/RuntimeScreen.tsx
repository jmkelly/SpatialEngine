import { useEffect, useState } from "react";
import type { PluginDto } from "@spatial/client";
import { providerStateLabel } from "../forms.ts";
import { useWorkbench, type RunOutcome } from "../state.tsx";
import { OutcomeView } from "./OverviewScreen.tsx";

interface Health {
  live: string | null;
  ready: {
    status: string | null;
    plugins: number;
  };
}

/**
 * Runtime health and plugin-replacement views (Phase 10, ADR-0031): the host
 * health endpoints, the supervised plugin workers with their lifecycle, and
 * the replacement demonstration controls — route new work to a version, drain
 * it, roll back — without stopping the host or this UI.
 */
export function RuntimeScreen() {
  const { client, plugins, actions, savedJobs } = useWorkbench();
  const [health, setHealth] = useState<Health>({ live: null, ready: { status: null, plugins: 0 } });
  const [busyPlugin, setBusyPlugin] = useState<string | null>(null);
  const [recent, setRecent] = useState<RunOutcome | null>(null);

  useEffect(() => {
    void refreshHealth();
  }, []);

  async function refreshHealth() {
    try {
      const live = await client.getHealthLive();
      const ready = await client.getHealthReady();
      setHealth({
        live: live.status ?? "?",
        ready: {
          status: ready.status ?? "?",
          plugins: ready.plugins ?? 0,
        },
      });
    } catch (error) {
      setHealth({ live: `unreachable (${describe(error)})`, ready: { status: "unreachable", plugins: 0 } });
    }
  }

  async function control(plugin: PluginDto, action: "route-new-work" | "drain" | "rollback") {
    setBusyPlugin(plugin.id);
    try {
      switch (action) {
        case "route-new-work":
          await client.routeNewWork(plugin.id);
          break;
        case "drain":
          await client.drainPlugin(plugin.id);
          break;
        case "rollback":
          await client.rollbackPlugin(plugin.id);
          break;
      }
      await actions.refreshPlugins();
      if (action === "route-new-work") {
        setRecent({
          capability: "(routing)",
          provider: plugin.id,
          ok: true,
          error: null,
          result: null,
          jobId: null,
          step: "active preference",
          finishedAt: new Date().toISOString(),
        });
      }
    } catch (error) {
      setRecent({ capability: "(control)", provider: plugin.id, ok: false, error: describe(error), result: null, jobId: null, step: null, finishedAt: new Date().toISOString() });
    } finally {
      setBusyPlugin(null);
    }
  }

  return (
    <section className="screen runtime-screen">
      <h2>Runtime health</h2>
      <div className="health-cards" data-testid="health">
        <div className={`health-card ${health.live === "live" ? "ok" : "bad"}`}>
          <span className="label">LIVE</span>
          <span className="value">{health.live ?? "…"}</span>
        </div>
        <div className={`health-card ${health.ready.status === "ready" ? "ok" : "bad"}`}>
          <span className="label">READY</span>
          <span className="value">{health.ready.status ?? "…"}</span>
          <small>plugins: {health.ready.plugins}</small>
        </div>
        <button className="ghost" onClick={() => void refreshHealth()}>re-check</button>
      </div>

      <h2>Plugin workers</h2>
      {plugins.length === 0 ? (
        <p className="empty">No supervised plugin workers (Spatial:PackagesRoot empty).</p>
      ) : (
        <div className="plugin-list">
          {plugins.map((plugin) => (
            <div className={`plugin-card state-${plugin.state.toLowerCase()}`} key={plugin.id} data-testid={`plugin-${plugin.id}`}>
              <div className="plugin-head">
                <code>{plugin.id}</code>
                <span className={`state ${plugin.state.toLowerCase()}`}>{providerStateLabel(plugin.state)}</span>
                {plugin.processId !== null && <span className="muted">pid {plugin.processId}</span>}
                {plugin.lastError !== null && <span className="plugin-error" title={plugin.lastError}>⚠ {plugin.lastError.slice(0, 120)}</span>}
              </div>
              <div className="plugin-meta muted small">
                restarts {String(plugin.restartCount)} · healthy-at {plugin.lastHealthyAt ?? "—"}
              </div>
              <details className="plugin-capabilities">
                <summary>capabilities ({plugin.capabilities.length})</summary>
                <ul>
                  {plugin.capabilities.map((capability) => (
                    <li key={capability.id}><code>{capability.id}</code> <span className="muted">{capability.traits.join(", ")}</span></li>
                  ))}
                </ul>
              </details>
              <div className="plugin-actions">
                <button
                  className="primary"
                  disabled={busyPlugin === plugin.id || plugin.state !== "Active"}
                  onClick={() => void control(plugin, "route-new-work")}
                  data-testid={`route-${plugin.id}`}
                >
                  route new work here
                </button>
                <button
                  className="danger"
                  disabled={busyPlugin === plugin.id || plugin.state !== "Active"}
                  onClick={() => void control(plugin, "drain")}
                  data-testid={`drain-${plugin.id}`}
                >
                  drain
                </button>
                <button
                  className="ghost"
                  disabled={busyPlugin === plugin.id || plugin.state !== "Stopped"}
                  onClick={() => void control(plugin, "rollback")}
                  data-testid={`rollback-${plugin.id}`}
                >
                  rollback
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
      {recent !== null && (
        <div className="recent-control">
          <h3>Last control action</h3>
          <OutcomeView outcome={recent} />
        </div>
      )}

      <h2>Recent jobs</h2>
      {savedJobs.length === 0 ? (
        <p className="empty">No jobs observed in this browser yet.</p>
      ) : (
        <table className="grid">
          <thead><tr><th>Job</th><th>Capability</th><th>State</th><th>Provider</th><th>Started</th></tr></thead>
          <tbody>
            {savedJobs.slice(0, 15).map((job) => (
              <tr key={job.id}>
                <td><code>{job.id}</code></td>
                <td>{job.capability}</td>
                <td><span className={`state ${job.state.toLowerCase()}`}>{job.state}</span></td>
                <td>{job.provider ?? "—"}</td>
                <td>{new Date(job.startedAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}