#!/usr/bin/env node
// Static proof-page server for T-068 (test scaffolding, not product): serves
// the OpenLayers/Leaflet client-compat pages with their npm-bundled clients
// (never a CDN, deterministic/offline) and reverse-proxies /ogc + /api to the
// WORKBENCH_URL host, so the pages are same-origin with the services they
// prove — no CORS, no taint, genuine client HTTP end to end.
//
// Started by Playwright's webServer (playwright.config.ts); Playwright injects
// PORT, WORKBENCH_URL is inherited from eng/workbench-e2e.sh.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { dirname, join, normalize, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const here = dirname(fileURLToPath(import.meta.url));
const port = Number(process.env.PORT ?? process.env.PROOF_PORT ?? 5898);
const target = (process.env.WORKBENCH_URL ?? "http://127.0.0.1:5999").replace(/\/$/, "");

const entries = ["ol-proof.entry.js", "leaflet-proof.entry.js"];

await build({
  entryPoints: entries.map((entry) => join(here, entry)),
  bundle: true,
  outdir: here,
  outExtension: { ".js": ".bundle.js" },
  loader: { ".png": "file", ".css": "css" },
  assetNames: "proof-assets/[name]-[hash]",
});
console.log(`[proof-server] bundled ${entries.join(", ")} from npm (offline)`);

const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".png": "image/png",
  ".json": "application/json",
};

const server = createServer(async (clientRequest, clientResponse) => {
  try {
    const url = new URL(clientRequest.url ?? "/", "http://proof");
    if (url.pathname === "/healthz") {
      clientResponse.writeHead(200, { "content-type": "text/plain" });
      clientResponse.end("ok");
      return;
    }
    if (url.pathname.startsWith("/ogc/") || url.pathname.startsWith("/api/")) {
      await proxy(clientRequest, clientResponse, url);
      return;
    }
    await serveStatic(clientResponse, url.pathname);
  } catch (error) {
    console.error(`[proof-server] ${error.message}`);
    if (!clientResponse.headersSent) clientResponse.writeHead(500);
    clientResponse.end("proof-server error");
  }
});

async function serveStatic(clientResponse, pathname) {
  const file = normalize(join(here, pathname === "/" ? "ol-proof.html" : pathname.slice(1)));
  if (!file.startsWith(here + sep) && file !== here) {
    clientResponse.writeHead(403);
    clientResponse.end("forbidden");
    return;
  }
  const dot = file.lastIndexOf(".");
  const type = contentTypes[file.slice(dot)] ?? "application/octet-stream";
  try {
    const body = await readFile(file);
    clientResponse.writeHead(200, { "content-type": type, "content-length": body.length });
    clientResponse.end(body);
  } catch {
    clientResponse.writeHead(404);
    clientResponse.end("not found");
  }
}

// Byte-identical pass-through: the host sees the genuine client request.
function proxy(clientRequest, clientResponse, url) {
  return new Promise((resolve) => {
    const headers = { ...clientRequest.headers, host: new URL(target).host };
    delete headers["connection"];
    const upstream = new URL(target + url.pathname + url.search);
    const protocol = upstream.protocol === "https:" ? "https:" : "http:";
    const transport = protocol === "https:" ? import("node:https") : import("node:http");
    transport.then(({ request }) => {
      const outgoing = request(
        {
          protocol,
          hostname: upstream.hostname,
          port: upstream.port,
          path: upstream.pathname + upstream.search,
          method: clientRequest.method,
          headers,
        },
        (incoming) => {
          const responseHeaders = { ...incoming.headers, "access-control-allow-origin": "*" };
          delete responseHeaders["connection"];
          delete responseHeaders["transfer-encoding"];
          clientResponse.writeHead(incoming.statusCode ?? 502, responseHeaders);
          incoming.pipe(clientResponse);
          incoming.on("end", resolve);
        },
      );
      outgoing.on("error", (error) => {
        if (!clientResponse.headersSent) clientResponse.writeHead(502);
        clientResponse.end(`proxy error: ${error.message}`);
        resolve();
      });
      clientRequest.pipe(outgoing);
    });
  });
}

server.listen(port, "127.0.0.1", () => {
  console.log(`[proof-server] proof pages at http://127.0.0.1:${port}/ proxying ${target}`);
});
