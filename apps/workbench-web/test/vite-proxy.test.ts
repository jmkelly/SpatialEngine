import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const configPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "vite.config.ts");
const configText = readFileSync(configPath, "utf8");

test("the Vite dev server proxies /arcgis to the host so parity URLs hit the host port", () => {
  assert.ok(
    configText.includes('"/arcgis"') || configText.includes("'/arcgis'"),
    "vite.config.ts must proxy /arcgis to VITE_DEV_HOST (hostBaseUrl falls back to the Vite origin)",
  );
});
