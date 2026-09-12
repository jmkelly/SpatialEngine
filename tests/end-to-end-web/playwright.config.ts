import { defineConfig } from "@playwright/test";

/**
 * The workbench browser tests (plan §18): run against the real
 * Spatial.Host serving the built workbench with its in-process services.
 * The host lifecycle belongs to eng/workbench-e2e.sh — Playwright only
 * points at WORKBENCH_URL and runs. "Web tests run the browser workbench
 * independently using Playwright. Tauri must not be needed for these tests."
 */
const baseUrl = process.env.WORKBENCH_URL ?? "http://127.0.0.1:5999";

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