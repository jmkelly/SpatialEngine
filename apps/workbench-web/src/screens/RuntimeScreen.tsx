import { useEffect, useState } from "react";
import { useWorkbench, type RunOutcome } from "../state.tsx";
import { OutcomeView } from "./OverviewScreen.tsx";

interface Health {
  live: string | null;
  ready: {
    status: string | null;
    stores: string[];
  };
}

/**
 * Runtime health and recent runs: the host health endpoints plus the
 * operations this browser has run (persisted across reloads).
 */
export function RuntimeScreen() {
  const { client, savedRuns, recentOutcome } = useWorkbench();
  const [health, setHealth] = useState<Health>({ live: null, ready: { status: null, stores: [] } });

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
          stores: ready.stores ?? [],
        },
      });
    } catch (error) {
      setHealth({ live: `unreachable (${describe(error)})`, ready: { status: "unreachable", stores: [] } });
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
          <small>stores: {health.ready.stores.join(", ") || "…"}</small>
        </div>
        <button className="ghost" onClick={() => void refreshHealth()}>re-check</button>
      </div>

      {recentOutcome !== null && (
        <div className="recent-control">
          <h3>Last operation</h3>
          <OutcomeView outcome={recentOutcome} />
        </div>
      )}

      <h2>Recent runs</h2>
      {savedRuns.length === 0 ? (
        <p className="empty">No operations run in this browser yet.</p>
      ) : (
        <table className="grid">
          <thead><tr><th>Operation</th><th>Result</th><th>Detail</th><th>Started</th></tr></thead>
          <tbody>
            {savedRuns.slice(0, 15).map((run) => (
              <tr key={run.id}>
                <td><code>{run.op}</code></td>
                <td><span className={`state ${run.ok ? "completed" : "failed"}`}>{run.ok ? "ok" : "failed"}</span></td>
                <td>{run.detail ?? "—"}</td>
                <td>{new Date(run.startedAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

export type { RunOutcome };

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
