import { test as base, expect, type Page, chromium } from "@playwright/test";

// A fresh browser per test: headless WebGL rendering degrades after several
// contexts in one Chromium process (feature queries stall on readPixels and
// later tests see empty frames). Isolating the browser process per test keeps
// the map rendering deterministic.
export const test = base.extend({
  browser: async ({}, use) => {
    const browser = await chromium.launch({ args: ["--disable-dev-shm-usage", "--disable-gpu"] });
    await use(browser);
    await browser.close();
  },
});

// Capture renderer errors for failure diagnostics.
test.beforeEach(async ({ page }) => {
  page.on("pageerror", (error) => {
    console.log(`[pageerror] ${String(error).slice(0, 300)}`);
  });
  page.on("console", (message) => {
    if (message.type() === "error") console.log(`[console.error] ${message.text().slice(0, 300)}`);
  });
});

/**
 * The workbench browser suite (Phase 10, plan §18 web tests): catalogue, map
 * and selection, capability forms, job progress, result preview and
 * persistence, runtime health and the plugin-replacement demonstration — all
 * through a real browser against the real host; no Tauri.
 */

async function openTab(page: Page, label: string) {
  await page.getByRole("tab", { name: label, exact: true }).click();
}

/** Loads demo.points, waits for the fitted camera, and clicks the pixel of lng/lat (0,0). */
async function selectGridCentre(page: Page) {
  await page.getByTestId("dataset-select").selectOption("demo.points");
  await expect(page.getByTestId("feature-count")).toHaveText("110 features", { timeout: 20_000 });
  await expect(page.getByTestId("selection")).toContainText("Click a feature");
  // The map must be loaded and fitted so selection matching is final.
  await page.waitForFunction(
    () => {
      const map = (window as unknown as { __spatialMap?: { loaded: () => boolean; getZoom: () => number } }).__spatialMap;
      return map !== undefined && map.loaded() === true && map.getZoom() > 3;
    },
    { polling: "interval", interval: 250 },
    { timeout: 60_000 },
  );
  // Drive the map's own click pipeline at lng/lat (0,0) — the selection is
  // coordinate-based (no renderer pixel reads), so no frame is required.
  for (let attempt = 0; attempt < 5; attempt++) {
    await page.evaluate(() => {
      const map = (window as unknown as { __spatialMap: { fire: (event: string, data: object) => void } }).__spatialMap;
      map.fire("click", { point: [0, 0], lngLat: { lng: 0, lat: 0 }, originalEvent: new MouseEvent("click") });
    });
    await page.waitForTimeout(300);
    if ((await page.getByTestId("selection").textContent())?.includes("point-")) {
      await expect(page.getByTestId("selection")).toContainText("point-");
      return;
    }
  }
  throw new Error("selecting the grid centre never succeeded");
}

test("the workbench loads from the host with the full catalogue", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveTitle(/Spatial Engine — Workbench/);

  // The header shows the loaded plugins (nts@1, nts@2, demo@1, postgis@1)
  // and the capabilities count — the Explore screen lists both surfaces.
  await expect(page.getByText("4 plugins").first()).toBeVisible({ timeout: 20_000 });
  const providers = page.locator("tbody tr");
  await expect(providers).toHaveCount(4);
  await expect(page.getByText("nts@1").first()).toBeVisible();
  await expect(page.getByText("nts@2").first()).toBeVisible();
  await expect(page.getByText("demo@1").first()).toBeVisible();
  await expect(page.getByText("postgis@1").first()).toBeVisible();

  // The capability catalogue includes the standard operations and the demo
  // catalogue/scan contracts.
  await expect(page.getByText("spatial.geometry.buffer@1")).toBeVisible();
  await expect(page.getByText("spatial.feature.scan@1").first()).toBeVisible();
});

test("the catalogue lists the demo datasets from the demo provider", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByText("Catalogue datasets")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText("demo.points").first()).toBeVisible();
  await expect(page.getByText("demo.cities").first()).toBeVisible();
});

test("browse a dataset, render it on the map and select a feature", async ({ page }) => {
  await page.goto("/");
  await openTab(page, "Map");
  await selectGridCentre(page);

  // The selection panel shows the picked feature's attributes.
  await expect(page.getByTestId("selection")).toContainText("Selected");
  await expect(page.getByTestId("selection")).toContainText("name");
  await expect(page.getByTestId("selection")).toContainText("value");
});

test("invoke a buffer on the selected feature, preview and persist the result", async ({ page }) => {
  await page.goto("/");
  await openTab(page, "Map");
  await selectGridCentre(page);

  // The Run screen's buffer form is pre-filled from the selection.
  await openTab(page, "Run");
  await page.getByTestId("capability-select").selectOption("spatial.geometry.buffer@1");
  await expect(page.getByTestId("field-geometry").locator("input")).toHaveValue("selected ✓");

  await page.getByTestId("invoke-button").click();
  const outcome = page.getByTestId("outcome");
  await expect(outcome).toContainText("completed", { timeout: 30_000 });
  await expect(outcome).toContainText("nts@1");
  await expect(page.getByTestId("result-json")).toContainText("$geometry");

  // Persist the result and see it come back after a reload.
  await page.getByTestId("save-name").fill("buffered point");
  await page.getByTestId("save-button").click();
  await expect(page.getByTestId("saved-result").first()).toContainText("buffered point");

  await page.reload();
  await openTab(page, "Run");
  await expect(page.getByTestId("saved-result").first()).toContainText("buffered point");
});

test("a long-running demo job streams progress to completion", async ({ page }) => {
  await page.goto("/");
  await openTab(page, "Run");
  await page.getByTestId("capability-select").selectOption("spatial.demo.sleep@1");
  await page.locator('input[type="number"]').fill("1200");

  await page.getByTestId("invoke-button").click();
  await expect(page.getByTestId("outcome")).toContainText("completed", { timeout: 30_000 });
  await expect(page.getByTestId("outcome")).toContainText("demo@1");
  await expect(page.getByTestId("outcome")).toContainText("job");
});

test("runtime health and the plugin-replacement demonstration", async ({ page }) => {
  await page.goto("/");
  await openTab(page, "Runtime");

  // Health cards reflect the live/ready host.
  await expect(page.getByTestId("health")).toContainText("LIVE");
  await expect(page.getByTestId("health")).toContainText("ready");

  // The workers are listed with their lifecycle.
  for (const id of ["nts@1", "nts@2", "demo@1", "postgis@1"]) {
    await expect(page.getByTestId(`plugin-${id}`)).toContainText("active");
  }

  // Pick a feature so the buffer form is enabled.
  await openTab(page, "Map");
  await selectGridCentre(page);

  // Route new buffer work to nts@2 and prove provenance switches.
  await openTab(page, "Runtime");
  await page.getByTestId("route-nts@2").click();
  await openTab(page, "Run");
  await page.getByTestId("capability-select").selectOption("spatial.geometry.buffer@1");
  await expect(page.getByTestId("field-geometry").locator("input")).toHaveValue("selected ✓");
  await page.getByTestId("invoke-button").click();
  const outcome = page.getByTestId("outcome");
  await expect(outcome).toContainText("completed", { timeout: 30_000 });
  await expect(outcome).toContainText("served by nts@2");

  // Drain nts@1: the host keeps serving, new work still goes to nts@2.
  await openTab(page, "Runtime");
  await page.getByTestId("drain-nts@1").click();
  await expect(page.getByTestId("plugin-nts@1")).toContainText("stopped", { timeout: 30_000 });
  await expect(page.getByTestId("route-nts@1")).toBeDisabled();

  // Roll back: nts@1 reactivates and routes new work back to it.
  await page.getByTestId("rollback-nts@1").click();
  await expect(page.getByTestId("plugin-nts@1")).toContainText("active", { timeout: 40_000 });
  await expect(page.getByTestId("route-nts@1")).not.toBeDisabled();
});