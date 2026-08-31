#!/usr/bin/env node
/**
 * Drift check for the generated TS types: regenerates into a temp file from
 * the committed OpenAPI snapshot and fails when `src/generated-types.ts` has
 * drifted — the "the SDK matches the contract" gate. Runs inside `npm test`;
 * refresh with `npm run generate` after a host contract change.
 */
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const committed = readFileSync(join(root, "src", "generated-types.ts"), "utf8");

const temp = mkdtempSync(join(tmpdir(), "spatial-ts-gen-"));
try {
  const candidate = join(temp, "generated-types.ts");
  execFileSync(process.execPath, [
    join(root, "scripts", "generate.mjs"),
    join(root, "scripts", "openapi.snapshot.json"),
    candidate,
  ]);
  const regenerated = readFileSync(candidate, "utf8");
  if (committed !== regenerated) {
    console.error(
      "src/generated-types.ts has drifted from the OpenAPI snapshot.\n" +
        "Run `npm run generate` and commit the refreshed file.",
    );
    process.exit(1);
  }

  console.log("generated-types.ts matches the OpenAPI snapshot");
} finally {
  rmSync(temp, { recursive: true, force: true });
}