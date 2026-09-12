import { test as base, expect, type Page, chromium } from "@playwright/test";

// A fresh browser per test: headless WebGL rendering degrades after several
// contexts in one Chromium process. Isolating the browser process per test
// keeps the map rendering deterministic.
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
 * The workbench browser suite (ADR-0033): catalogue,
 * map and selection, operation forms, sleep cancellation, result preview
 * and persistence, runtime health — all through a real browser against the
 * real host.
 *
 * Every test opens `/?basemap=none`: the basemap raster is presentation
 * only, and the suite stays deterministic without network tiles.
 */

async function openTab(page: Page, label: string) {
  await page.getByRole("tab", { name: label, exact: true }).click();
}

/** Uploads a one-point GeoJSON through the composer's inline import form. */
async function uploadPointLayer(page: Page, fileName: string, lon: number, lat: number) {
  const body = JSON.stringify({
    type: "FeatureCollection",
    features: [
      { type: "Feature", geometry: { type: "Point", coordinates: [lon, lat] }, properties: { name: fileName } },
    ],
  });
  await page.getByTestId("composer-file").setInputFiles({
    name: fileName,
    mimeType: "application/geo+json",
    buffer: Buffer.from(body),
  });
  await page.getByTestId("composer-upload").click();
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

test("the workbench loads from the host with the service catalogue", async ({ page }) => {
  await page.goto("/?basemap=none");
  await expect(page).toHaveTitle(/Spatial Engine — Workbench/);

  // The Explore screen lists the typed operations and the demo datasets.
  await expect(page.getByText("POST /api/geometry/buffer").first()).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText("Catalogue datasets")).toBeVisible();
  await expect(page.getByText("demo.points").first()).toBeVisible();
  await expect(page.getByText("demo.cities").first()).toBeVisible();
});

test("browse a dataset, render it on the map and select a feature", async ({ page }) => {
  await page.goto("/?basemap=none");
  await openTab(page, "Map");
  await selectGridCentre(page);

  // The selection panel shows the picked feature's attributes.
  await expect(page.getByTestId("selection")).toContainText("Selected");
  await expect(page.getByTestId("selection")).toContainText("name");
  await expect(page.getByTestId("selection")).toContainText("value");
});

test("run a buffer on the selected feature, preview and persist the result", async ({ page }) => {
  await page.goto("/?basemap=none");
  await openTab(page, "Map");
  await selectGridCentre(page);

  // The Run screen's buffer form is pre-filled from the selection.
  await openTab(page, "Run");
  await page.getByTestId("capability-select").selectOption("buffer");
  await expect(page.getByTestId("field-geometry").locator("input")).toHaveValue("selected ✓");

  await page.getByTestId("invoke-button").click();
  const outcome = page.getByTestId("outcome");
  await expect(outcome).toContainText("completed", { timeout: 30_000 });
  await expect(page.getByTestId("result-summary")).toContainText("buffered");

  // Persist the result and see it come back after a reload.
  await page.getByTestId("save-name").fill("buffered point");
  await page.getByTestId("save-button").click();
  await expect(page.getByTestId("saved-result").first()).toContainText("buffered point");

  await page.reload();
  await openTab(page, "Run");
  await expect(page.getByTestId("saved-result").first()).toContainText("buffered point");
});

test("clear results drops the unsaved preview but keeps saved results", async ({ page }) => {
  await page.goto("/?basemap=none");
  await openTab(page, "Map");
  await selectGridCentre(page);

  // Run a buffer: its geometry lands on the map as one transient preview.
  await openTab(page, "Run");
  await page.getByTestId("capability-select").selectOption("buffer");
  await page.getByTestId("invoke-button").click();
  await expect(page.getByTestId("result-summary")).toContainText("buffered", { timeout: 30_000 });
  await openTab(page, "Map");
  await expect(page.getByTestId("result-count")).toHaveText("1 result");

  // Persisting adds a second, saved copy of the same geometry.
  await openTab(page, "Run");
  await page.getByTestId("save-name").fill("kept result");
  await page.getByTestId("save-button").click();
  await expect(page.getByTestId("saved-result").first()).toContainText("kept result");
  await openTab(page, "Map");
  await expect(page.getByTestId("result-count")).toHaveText("2 results");

  // Clearing removes only the transient preview; the saved feature stays.
  await page.getByTestId("clear-results").click();
  await expect(page.getByTestId("result-count")).toHaveText("1 result");
  await expect(page.getByTestId("clear-results")).toBeDisabled();
});

test("a sleep can be cancelled while running", async ({ page }) => {
  await page.goto("/?basemap=none");
  await openTab(page, "Run");
  await page.getByTestId("capability-select").selectOption("sleep");
  await page.locator('input[type="number"]').fill("30000");

  await page.getByTestId("invoke-button").click();
  await expect(page.getByTestId("job-progress")).toBeVisible({ timeout: 10_000 });
  await page.getByTestId("cancel-button").click();
  await expect(page.getByTestId("outcome")).toContainText("cancelled", { timeout: 30_000 });
});

test("runtime health reports the host and its stores", async ({ page }) => {
  await page.goto("/?basemap=none");
  await openTab(page, "Runtime");

  // Health cards reflect the live/ready host.
  await expect(page.getByTestId("health")).toContainText("LIVE");
  await expect(page.getByTestId("health")).toContainText("ready");
  await expect(page.getByTestId("health")).toContainText("demo");
});

test("a GeoJSON upload is ingested and published as a feature service", async ({ page }) => {
  await page.goto("/?basemap=none");
  await openTab(page, "Data");

  await page.getByTestId("admin-token").fill("workbench-e2e-token");
  const geojson = JSON.stringify({
    type: "FeatureCollection",
    features: [
      { type: "Feature", geometry: { type: "Point", coordinates: [13.4, 52.5] }, properties: { name: "Berlin", population: 3664000 } },
      { type: "Feature", geometry: { type: "Point", coordinates: [2.35, 48.85] }, properties: { name: "Paris", population: 2150000 } },
    ],
  });
  await page.getByTestId("upload-file").setInputFiles({
    name: "cities.geojson",
    mimeType: "application/geo+json",
    buffer: Buffer.from(geojson),
  });
  await page.getByTestId("dataset").fill("public.e2e_cities");
  await page.getByTestId("publish").fill("e2e_cities");

  await page.getByTestId("upload").click();

  await expect(page.getByTestId("ingest-result")).toContainText("public.e2e_cities", { timeout: 30_000 });
  await expect(page.getByTestId("ingest-result")).toContainText("2 feature(s)");
  await expect(page.getByTestId("publications")).toContainText("e2e_cities");
});

test("the map composer uploads, styles, reorders and publishes layers", async ({ page }) => {
  // The run's publications file is isolated, but never assume a clean name.
  await page.request.delete("/api/publications/composer_e2e", {
    headers: { authorization: "Bearer workbench-e2e-token" },
  });
  await page.goto("/?basemap=none");
  await openTab(page, "Composer");

  await page.getByTestId("composer-store").selectOption("memory");
  await page.getByTestId("composer-token").fill("workbench-e2e-token");

  await uploadPointLayer(page, "composer_a.geojson", 10, 10);
  await expect(page.getByTestId("composer-layer")).toHaveCount(1, { timeout: 30_000 });
  await expect(page.getByTestId("composer-status")).toContainText("Imported 1 feature(s)");

  await uploadPointLayer(page, "composer_b.geojson", 20, 20);
  await expect(page.getByTestId("composer-layer")).toHaveCount(2, { timeout: 30_000 });
  await expect(page.getByTestId("composer-layer").nth(0)).toContainText("public.composer_a");

  // Style: open the editor and flip layer visibility.
  await page.getByTestId("layer-style").first().click();
  await expect(page.getByTestId("style-editor")).toBeVisible();
  const eye = page.getByTestId("layer-visibility").first();
  await expect(eye).toHaveText("◉");
  await eye.click();
  await expect(eye).toHaveText("○");

  // Reorder: the accessible control moves the top layer down.
  await page.getByTestId("composer-layer").nth(0).getByTestId("layer-down").click();
  await expect(page.getByTestId("composer-layer").nth(0)).toContainText("public.composer_b");

  // Publish the ordered composition as a map service.
  await page.getByTestId("composer-name").fill("composer_e2e");
  await page.getByTestId("composer-kind").selectOption("map");
  await page.getByTestId("composer-publish").click();
  await expect(page.getByTestId("composer-status")).toContainText("Published composer_e2e", { timeout: 30_000 });
  await expect(page.getByTestId("composer-publications")).toContainText("composer_e2e");
  await expect(page.getByTestId("composer-publications")).toContainText("2 layer(s)");
});

test("the map composer reorders layers by drag and drop", async ({ page }) => {
  await page.goto("/?basemap=none");
  await openTab(page, "Composer");
  await page.getByTestId("composer-store").selectOption("memory");
  await page.getByTestId("composer-token").fill("workbench-e2e-token");

  await uploadPointLayer(page, "drag_a.geojson", 10, 10);
  await expect(page.getByTestId("composer-layer")).toHaveCount(1, { timeout: 30_000 });
  await uploadPointLayer(page, "drag_b.geojson", 20, 20);
  await expect(page.getByTestId("composer-layer")).toHaveCount(2, { timeout: 30_000 });

  const first = page.getByTestId("composer-layer").nth(0);
  await expect(first).toContainText("public.drag_a");
  await first.dragTo(page.getByTestId("composer-layer").nth(1));
  await expect(page.getByTestId("composer-layer").nth(0)).toContainText("public.drag_b");
});
