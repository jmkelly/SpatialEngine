import { defineConfig } from "@playwright/test";

/**
 * The workbench browser tests (ADR-0033): run against the real
 * Spatial.Host serving the built workbench with its in-process services.
 * The host lifecycle belongs to eng/workbench-e2e.sh — Playwright only
 * points at WORKBENCH_URL and runs.
 */
const baseUrl = process.env.WORKBENCH_URL ?? "http://127.0.0.1:5999";

// The T-068 proof pages (static OL/Leaflet scaffolding with npm-bundled
// clients) are served here, reverse-proxying /ogc + /api to the host so the
// pages stay same-origin with the services they prove.

export default defineConfig({
  testDir: "./tests",
  timeout: 90_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: {
    baseURL: baseUrl,
    trace: "retain-on-failure",
  },
  webServer: {
    command: "node ./proof-server.mjs",
    port: Number(process.env.PROOF_PORT ?? 5898),
    timeout: 60_000,
    reuseExistingServer: !process.env.CI,
  },
  projects: [
    {
      name: "chromium",
      use: {
        browserName: "chromium",
        viewport: { width: 1440, height: 900 },
        launchOptions: {
          args: ["--disable-dev-shm-usage", "--disable-gpu"],
        },
      },
    },
  ],
});