import { useEffect, useMemo, useState, type FormEvent } from "react";
import { useWorkbench } from "./state.tsx";
import { createClient } from "./api.ts";
import { OverviewScreen } from "./screens/OverviewScreen.tsx";
import { MapScreen } from "./screens/MapScreen.tsx";
import { ComposerScreen } from "./screens/ComposerScreen.tsx";
import { RunScreen } from "./screens/RunScreen.tsx";
import { RuntimeScreen } from "./screens/RuntimeScreen.tsx";
import { DataScreen } from "./screens/DataScreen.tsx";
import { ParityScreen } from "./screens/ParityScreen.tsx";

type Tab = "explore" | "map" | "composer" | "run" | "data" | "parity" | "runtime";

const Tabs: { id: Tab; label: string; title: string }[] = [
  { id: "explore", label: "Explore", title: "Service catalogue and datasets" },
  { id: "map", label: "Map", title: "Dataset map with selection and attribute inspection" },
  { id: "composer", label: "Maps", title: "Compose, style and publish maps with services" },
  { id: "run", label: "Run", title: "Operation forms, cancellation, result preview and persistence" },
  { id: "data", label: "Data", title: "Upload data and publish feature maps" },
  { id: "parity", label: "Parity", title: "Side-by-side public Esri vs localhost parity panels" },
  { id: "runtime", label: "Runtime", title: "Host health and recent runs" },
];

/** The workbench shell: four screens over one host connection. */
export function App() {
  const { error, busy, catalogueDatasets, actions } = useWorkbench();
  const [tab, setTab] = useState<Tab>("explore");
  const [lastRefresh, setLastRefresh] = useState<string | null>(null);
  const authClient = useMemo(() => createClient(), []);
  const [authOpen, setAuthOpen] = useState(false);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);

  async function login(event: FormEvent) {
    event.preventDefault();
    setAuthError(null);
    try {
      const result = await authClient.login(username, password);
      if (remember) window.localStorage.setItem("spatial.auth.token", result.token);
      setPassword("");
      setAuthOpen(false);
    } catch (error) {
      setAuthError(error instanceof Error ? error.message : "Authentication failed.");
    }
  }

  async function logout() {
    try {
      await authClient.logout();
    } catch {
      // A token may already be revoked; local cleanup is still safe.
    }
    window.localStorage.removeItem("spatial.auth.token");
    setAuthOpen(true);
  }

  useEffect(() => {
    void actions.refreshAll().then(() => setLastRefresh(new Date().toLocaleTimeString()));
  }, [actions]);

  useEffect(() => {
    const requireLogin = () => setAuthOpen(true);
    window.addEventListener("spatial:auth-required", requireLogin);
    return () => window.removeEventListener("spatial:auth-required", requireLogin);
  }, []);

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
          <button className="ghost" onClick={() => setAuthOpen(true)}>login</button>
        </div>
      </header>
      {authOpen && (
        <div className="auth-panel" role="dialog" aria-label="Sign in">
          <form onSubmit={login}>
            <strong>Sign in for mutations</strong>
            <input aria-label="Username" value={username} onChange={(event) => setUsername(event.target.value)} />
            <input aria-label="Password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} />
            <label><input type="checkbox" checked={remember} onChange={(event) => setRemember(event.target.checked)} /> remember on this device</label>
            {authError !== null && <span role="alert"> {authError}</span>}
            <button type="submit">login</button>
            <button type="button" className="ghost" onClick={() => setAuthOpen(false)}>close</button>
            {window.localStorage.getItem("spatial.auth.token") !== null && <button type="button" onClick={() => void logout()}>logout</button>}
          </form>
        </div>
      )}
      {error !== null && (
        <div className="error-banner" role="alert">
          <span>⚠</span> {error}
          <button className="ghost" onClick={() => actions.refreshAll()}>retry</button>
        </div>
      )}
      <main className="content">
        {tab === "explore" && <OverviewScreen />}
        {tab === "map" && <MapScreen />}
        {tab === "composer" && <ComposerScreen />}
        {tab === "run" && <RunScreen />}
        {tab === "data" && <DataScreen />}
        {tab === "parity" && <ParityScreen />}
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
