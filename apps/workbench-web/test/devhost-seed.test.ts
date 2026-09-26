import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const programPath = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "..",
  "..",
  "src",
  "Spatial.DevHost",
  "Program.cs",
);
const programText = readFileSync(programPath, "utf8");

test("the DevHost seeds the Census map on boot so restarts do not wipe the Parity page's localhost panel", () => {
  assert.ok(
    programText.includes("seed.mjs"),
    "Program.cs must run tools/seed/seed.mjs as a boot step (the memory store is wiped on every restart)",
  );
  assert.ok(
    programText.includes("SPATIAL_SEED_HOST"),
    "the boot seed must target the composed host endpoint, not a hard-coded port",
  );
  assert.ok(
    programText.includes("WaitFor(host)"),
    "the boot seed must wait for the host to be healthy first",
  );
});
