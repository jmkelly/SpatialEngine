import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Dev convenience: when the workbench runs under the Vite dev server, proxy
// the host API to the locally running Spatial.Host (default 5199, overridable
// with VITE_DEV_HOST). The production build is served by the host itself
// (Spatial:WebRoot, ADR-0031) — same origin, no proxy involved.
const devHost = process.env.VITE_DEV_HOST ?? "http://127.0.0.1:5199";

export default defineConfig({
  plugins: [react()],
  server: {
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