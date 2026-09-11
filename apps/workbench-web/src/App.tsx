import { useEffect, useState } from "react";
import { useWorkbench } from "./state.tsx";
import { OverviewScreen } from "./screens/OverviewScreen.tsx";
import { MapScreen } from "./screens/MapScreen.tsx";
import { RunScreen } from "./screens/RunScreen.tsx";
import { RuntimeScreen } from "./screens/RuntimeScreen.tsx";

type Tab = "explore" | "map" | "run" | "runtime";

const Tabs: { id: Tab; label: string; title: string }[] = [
  { id: "explore", label: "Explore", title: "Service catalogue and datasets" },
  { id: "map", label: "Map", title: "Dataset map with selection and attribute inspection" },
  { id: "run", label: "Run", title: "Operation forms, cancellation, result preview and persistence" },
  { id: "runtime", label: "Runtime", title: "Host health and recent runs" },
];

/** The workbench shell: four screens over one host connection. */
export function App() {
  const { error, busy, catalogueDatasets, actions } = useWorkbench();
  const [tab, setTab] = useState<Tab>("explore");
  const [lastRefresh, setLastRefresh] = useState<string | null>(null);

  useEffect(() => {
    void actions.refreshAll().then(() => setLastRefresh(new Date().toLocaleTimeString()));
  }, [actions]);

  return (
    <div className="app">
      <header className="app-header">
        <div className="brand">
          <span className="brand-mark">◈</span>
          <span>SPATIAL ENGINE — WORKBENCH</span>
        </div>
        <nav className="tabs" role="tablist">
          {Tabs.map((item) => (
            <button
              key={item.id}
              role="tab"
              aria-selected={tab === item.id}
              className={`tab${tab === item.id ? " active" : ""}`}
              title={item.title}
              onClick={() => setTab(item.id)}
            >
              {item.label}
            </button>
          ))}
        </nav>
        <div className="host-status">
          <span className={`dot${error ? " danger" : busy ? " busy" : " ok"}`} />
          <span className="host-name" title={clientBaseName()}>{clientBaseName()}</span>
          <span className="counts">
            {catalogueDatasets.length} dataset{catalogueDatasets.length === 1 ? "" : "s"}
          </span>
          {lastRefresh !== null && <span className="refreshed">· {lastRefresh}</span>}
          <button className="ghost" onClick={() => { void actions.refreshAll(); setLastRefresh(new Date().toLocaleTimeString()); }}>refresh</button>
        </div>
      </header>
      {error !== null && (
        <div className="error-banner" role="alert">
          <span>⚠</span> {error}
          <button className="ghost" onClick={() => actions.refreshAll()}>retry</button>
        </div>
      )}
      <main className="content">
        {tab === "explore" && <OverviewScreen />}
        {tab === "map" && <MapScreen />}
        {tab === "run" && <RunScreen />}
        {tab === "runtime" && <RuntimeScreen />}
      </main>
    </div>
  );
}

function clientBaseName(): string {
  // The base URL is stripped of credentials/paths for display only.
  try {
    const url = new URL(window.location.href);
    return url.origin.replace(/^https?:\/\//, "");
  } catch {
    return "host";
  }
}
