import { test, after } from "node:test";
import assert from "node:assert/strict";
import { createServer, type Server } from "node:http";
import { SpatialClient } from "../src/client.ts";
import { CapabilityStreamError, SpatialApiError } from "../src/errors.ts";
import { decodeFeatureBatch } from "../src/feature-batch.ts";
import { toBase64, encode } from "../src/wire.ts";

/**
 * A tiny scriptable HTTP server that behaves like the spatial host for the
 * SDK's client tests: URL assertions, the camelCase wire contract, job
 * polling, stream decoding and structured error mapping — no .NET needed.
 */
function fakeHost(routes: Record<string, (req: import("node:http").IncomingMessage) => { status: number; body: string; contentType: string }>): Promise<{ url: string; server: Server; requests: string[] }> {
  const requests: string[] = [];
  const server = createServer((req, res) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    requests.push(`${req.method} ${url.pathname}`);
    const route = routes[url.pathname];
    if (!route) {
      res.writeHead(404, { "content-type": "application/json" });
      res.end("{}");
      return;
    }
    const answer = route(req);
    res.writeHead(answer.status, { "content-type": answer.contentType });
    res.end(answer.body);
  });
  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      resolve({ url: `http://127.0.0.1:${typeof address === "object" && address ? address.port : 0}`, server, requests });
    });
  });
}

const capabilities = [
  {
    id: "fixture.echo@1",
    purpose: "Echoes",
    inputSchema: "scalar",
    outputSchema: "scalar",
    traits: ["Cancellable"],
    requiredPermissions: [],
    providers: ["fixture@1"],
  },
];

const completedEcho = {
  kind: "completed",
  capability: "fixture.echo@1",
  ok: true,
  result: "pong",
  error: null,
  provenance: { capability: "fixture.echo@1", provider: "fixture@1", step: "FirstHealthy", startedAt: "2026-09-01T00:00:00Z", durationMs: 1, deadline: null, jobId: null },
  job: null,
};

const completedJob = {
  kind: "job",
  capability: "fixture.sleep@1",
  ok: false,
  result: null,
  error: null,
  provenance: null,
  job: { jobId: "job-1", state: "pending", location: "/api/jobs/job-1" },
};

test("the client walks capabilities, invocation and job polling", async (t) => {
  let jobState = "running";
  const host = await fakeHost({
    "/api/capabilities": () => ({ status: 200, body: JSON.stringify(capabilities), contentType: "application/json" }),
    "/api/invocations": () => {
      if (jobState !== "running") return { status: 200, body: JSON.stringify(completedEcho), contentType: "application/json" };
      return { status: 202, body: JSON.stringify(completedJob), contentType: "application/json" };
    },
    "/api/jobs/job-1": () => {
      jobState = "completed";
      return {
        status: 200,
        body: JSON.stringify({
          id: "job-1",
          capability: "fixture.sleep@1",
          state: jobState,
          createdAt: "2026-09-01T00:00:00Z",
          startedAt: "2026-09-01T00:00:00Z",
          completedAt: "2026-09-01T00:00:02Z",
          deadline: null,
          provider: "fixture@1",
          step: "FirstHealthy",
          errorCode: null,
        }),
        contentType: "application/json",
      };
    },
  });

  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const listed = await client.getCapabilities();
  assert.equal(listed[0]?.id, "fixture.echo@1");

  const invocation = await client.invoke({ capability: "fixture.sleep@1" });
  assert.equal(invocation.kind, "job");
  assert.equal(invocation.job?.jobId, "job-1");

  const finished = await client.waitForJob("job-1", 5);
  assert.equal(finished.state, "completed");

  const echo = await client.invoke({ capability: "fixture.echo@1" });
  assert.equal(echo.kind, "completed");
  assert.equal(echo.ok, true);
  assert.equal(echo.result, "pong");

  assert.deepEqual(host.requests, [
    "GET /api/capabilities",
    "POST /api/invocations",
    "GET /api/jobs/job-1",
    "POST /api/invocations",
  ]);
});

test("404s surface as SpatialApiError", async (t) => {
  const host = await fakeHost({});
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  await assert.rejects(() => client.getCapability("spatial.missing@1"), (error: unknown) => {
    assert.ok(error instanceof SpatialApiError);
    assert.equal((error as SpatialApiError).status, 404);
    return true;
  });
});

test("streams decode items and throw CapabilityStreamError on an error line", async (t) => {
  const ndjson = JSON.stringify("chunk 1") + "\n" + JSON.stringify(encode(new Uint8Array([7, 8]))) + "\n";
  const failing = JSON.stringify("partial") + "\n" + JSON.stringify({ $error: { kind: "ProviderFailure", code: "provider.failure", message: "boom" } }) + "\n";
  const host = await fakeHost({
    "/api/resources/stream-1/stream": () => ({ status: 200, body: ndjson, contentType: "application/x-ndjson" }),
    "/api/resources/stream-2/stream": () => ({ status: 200, body: failing, contentType: "application/x-ndjson" }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const items: unknown[] = [];
  for await (const item of client.readStream("stream-1")) items.push(item);
  assert.equal(items[0], "chunk 1");
  assert.deepEqual(items[1], new Uint8Array([7, 8]));

  await assert.rejects(
    (async () => {
      for await (const _ of client.readStream("stream-2")) {
        // consume
      }
    })(),
    (error: unknown) => {
      assert.ok(error instanceof CapabilityStreamError);
      assert.equal((error as CapabilityStreamError).error.code, "provider.failure");
      return true;
    },
  );
});

// A tiny hand-built SFBAT batch (magic + version + one String|null schema +
// one feature) to prove readFeatureBatches decodes over the wire.
const sfbat = buildSfbat();

const plugin = {
  id: "nts@2",
  displayName: "NetTopologySuite operations v2",
  runtime: "dotnet",
  state: "Active",
  restartCount: 0,
  processId: 42,
  startedAt: "2026-09-01T00:00:00Z",
  lastHealthyAt: null,
  lastError: null,
  capabilities: [],
};

test("plugin control methods post to the control endpoints", async (t) => {
  const host = await fakeHost({
    "/api/plugins/nts%402/route-new-work": () => ({ status: 200, body: JSON.stringify(plugin), contentType: "application/json" }),
    "/api/plugins/nts%401/drain": () => ({ status: 200, body: JSON.stringify({ ...plugin, id: "nts@1", state: "Stopped" }), contentType: "application/json" }),
    "/api/plugins/nts%402/rollback": () => ({ status: 200, body: JSON.stringify(plugin), contentType: "application/json" }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const routed = await client.routeNewWork("nts@2");
  const drained = await client.drainPlugin("nts@1");
  const rolledBack = await client.rollbackPlugin("nts@2");

  assert.equal(routed.id, "nts@2");
  assert.equal(drained.state, "Stopped");
  assert.equal(rolledBack.id, "nts@2");
  assert.deepEqual(host.requests, [
    "POST /api/plugins/nts%402/route-new-work",
    "POST /api/plugins/nts%401/drain",
    "POST /api/plugins/nts%402/rollback",
  ]);
});

test("readFeatureBatches decodes canonical batches from the wire", async (t) => {
  const line = JSON.stringify(encode(sfbat));
  const host = await fakeHost({
    "/api/resources/scan-1/stream": () => ({ status: 200, body: line + "\n", contentType: "application/x-ndjson" }),
  });
  t.after(() => host.server.close());
  const client = new SpatialClient(host.url);

  const batches = [];
  for await (const batch of client.readFeatureBatches("scan-1")) batches.push(batch);
  assert.equal(batches.length, 1);
  const batch = batches[0]!;
  assert.equal(batch.features[0]?.id, "f1");
  const attribute = batch.features[0]?.attributes[0];
  assert.equal(attribute?.kind, "String");
  assert.equal(attribute?.kind === "String" ? attribute.value : null, "alice");
});

/** Builds the SFBAT v1 bytes for {"name": String} with one feature "f1" name="alice". */
function buildSfbat(): Uint8Array {
  const textEncoder = new TextEncoder();
  const parts: Uint8Array[] = [];
  const push = (bytes: Uint8Array) => parts.push(bytes);
  const pushString = (value: string) => {
    const bytes = textEncoder.encode(value);
    push(i32(bytes.length));
    push(bytes);
  };
  push(new TextEncoder().encode("SFBAT"));
  push(Uint8Array.of(1));
  push(i32(1));
  pushString("name");
  push(Uint8Array.of(4 /* String */, 1 /* nullable */, 0));
  push(i32(1));
  pushString("f1");
  push(Uint8Array.of(0));
  pushString("alice");
  return concat(parts);
}

function i32(value: number): Uint8Array {
  const bytes = new Uint8Array(4);
  new DataView(bytes.buffer).setInt32(0, value, true);
  return bytes;
}

function concat(chunks: Uint8Array[]): Uint8Array {
  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const result = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    result.set(chunk, offset);
    offset += chunk.length;
  }
  return result;
}

after(() => {
  // keep node --test from complaining about open handles
});