import { useCallback, useEffect, useState } from "react";
import { createClient } from "../api.ts";
import type { IngestResult, Publication } from "@spatial/client";

/**
 * The Data screen: upload a GeoJSON,
 * NDJSON or CSV file and load it into a dataset — optionally publishing it as
 * a feature service in the same call — then list the publications the host
 * serves. The admin token is host configuration and is only held in the
 * form field; the upload body is opaque bytes decoded by the host.
 */
export function DataScreen() {
  const client = createClient();
  const [token, setToken] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [dataset, setDataset] = useState("public.upload");
  const [srid, setSrid] = useState("4326");
  const [format, setFormat] = useState("geojson");
  const [store, setStore] = useState("memory");
  const [identity, setIdentity] = useState("auto");
  const [identityField, setIdentityField] = useState("");
  const [publish, setPublish] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<IngestResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [publications, setPublications] = useState<Publication[]>([]);

  const refresh = useCallback(async () => {
    try {
      setPublications(await client.listPublications());
    } catch (failure) {
      setError(messageOf(failure));
    }
  }, [client]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  async function upload() {
    if (file === null) {
      setError("Choose a file to upload first.");
      return;
    }

    setBusy(true);
    setError(null);
    setResult(null);
    try {
      const outcome = await client.ingest(
        file,
        file.name,
        {
          dataset,
          srid: Number.parseInt(srid, 10),
          format,
          store,
          identity,
          identityField: identity === "source" ? identityField : undefined,
          publish: publish.trim() === "" ? undefined : publish.trim(),
        },
        token === "" ? undefined : token,
      );
      setResult(outcome);
      await refresh();
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="screen data-screen">
      <h2>Upload &amp; publish data</h2>
      <p className="muted small">
        Load a foreign data file into an engine dataset with one atomic request.
        Give a publication name to expose it as a feature service immediately.
      </p>

      <div className="run-layout">
        <div className="form">
          <label className="field">
            <span>Admin token</span>
            <input
              type="password"
              data-testid="admin-token"
              value={token}
              placeholder="SPATIAL_ADMIN_TOKEN"
              onChange={(event) => setToken(event.target.value)}
            />
            <small>Required for mutations; never stored.</small>
          </label>

          <label className="field">
            <span>Data file</span>
            <input
              type="file"
              data-testid="upload-file"
              accept=".geojson,.json,.ndjson,.csv,text/csv,application/json"
              onChange={(event) => setFile(event.target.files?.[0] ?? null)}
            />
          </label>

          <label className="field">
            <span>Dataset</span>
            <input data-testid="dataset" value={dataset} onChange={(event) => setDataset(event.target.value)} />
          </label>

          <label className="field">
            <span>SRID</span>
            <input data-testid="srid" value={srid} onChange={(event) => setSrid(event.target.value)} />
          </label>

          <label className="field">
            <span>Format</span>
            <select data-testid="format" value={format} onChange={(event) => setFormat(event.target.value)}>
              <option value="geojson">GeoJSON</option>
              <option value="ndjson">Newline-delimited GeoJSON</option>
              <option value="csv">CSV</option>
            </select>
          </label>

          <label className="field">
            <span>Store</span>
            <select data-testid="store" value={store} onChange={(event) => setStore(event.target.value)}>
              <option value="memory">memory (ephemeral)</option>
              <option value="postgis">postgis</option>
            </select>
          </label>

          <label className="field">
            <span>Identity</span>
            <select data-testid="identity" value={identity} onChange={(event) => setIdentity(event.target.value)}>
              <option value="auto">auto (assigned)</option>
              <option value="none">none (query only)</option>
              <option value="source">source field</option>
            </select>
          </label>

          {identity === "source" && (
            <label className="field">
              <span>Identity field</span>
              <input value={identityField} onChange={(event) => setIdentityField(event.target.value)} />
            </label>
          )}

          <label className="field">
            <span>Publish as (optional)</span>
            <input data-testid="publish" value={publish} onChange={(event) => setPublish(event.target.value)} />
          </label>

          <div className="form-actions">
            <button data-testid="upload" disabled={busy} onClick={() => { void upload(); }}>
              {busy ? "Uploading…" : "Upload"}
            </button>
          </div>
        </div>

        <div>
          {error !== null && (
            <div className="outcome fail" role="alert">
              <div className="outcome-head">Upload failed</div>
              <p className="outcome-error">{error}</p>
            </div>
          )}

          {result !== null && (
            <div className="outcome ok" data-testid="ingest-result">
              <div className="outcome-head">
                <strong>{result.dataset}</strong>
                <span className="muted small">{result.features} feature(s) · SRID {result.srid}</span>
              </div>
              <p className="muted small">
                identity: {result.identityField ?? "none"}
                {result.publication ? ` · published as ${result.publication.name}` : ""}
              </p>
            </div>
          )}

          <h3>Publications</h3>
          {publications.length === 0 ? (
            <p className="empty">No publications.</p>
          ) : (
            <ul className="capability-list" data-testid="publications">
              {publications.map((publication) => (
                <li key={publication.name} className="capability-row">
                  <span className="capability-name">{publication.name}</span>
                  <span className="muted small">
                    {publication.kind} · {publication.layers.length} layer(s)
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </section>
  );
}

function messageOf(failure: unknown): string {
  return failure instanceof Error ? failure.message : String(failure);
}
