import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Dev convenience: when the workbench runs under the Vite dev server, proxy
// the host API to the locally running Spatial.Host (default 5199, overridable
// with VITE_DEV_HOST). The production build is served by the host itself
// (Spatial:WebRoot, ADR-0031) — same origin, no proxy involved.
const devHost = process.env.VITE_DEV_HOST ?? "http://127.0.0.1:5199";

// Dev convenience for remote access (e.g. over a private Tailscale network):
// VITE_DEV_BIND (usually 0.0.0.0) makes the dev server listen on an interface
// other than loopback and VITE_ALLOWED_HOSTS is a comma-separated Host-header
// allow-list (Vite rejects unknown hostnames by default). Both are unset in the
// normal local workflow, which keeps the historical defaults.
const devBind = process.env.VITE_DEV_BIND;
const allowedHosts = (process.env.VITE_ALLOWED_HOSTS ?? "")
  .split(",")
  .map((host) => host.trim())
  .filter(Boolean);

export default defineConfig({
  plugins: [react()],
  server: {
    // In remote mode the port is a contract (Aspire's target port and the
    // bookmarked tailnet URL), so fail fast on a conflict instead of
    // silently drifting to the next free port.
    ...(devBind ? { host: devBind, strictPort: true } : {}),
    ...(allowedHosts.length > 0 ? { allowedHosts } : {}),
    proxy: {
      "/api": devHost,
      "/health": devHost,
      "/openapi": devHost,
    },
  },
  build: {
    outDir: "dist",
    sourcemap: false,
  },
});