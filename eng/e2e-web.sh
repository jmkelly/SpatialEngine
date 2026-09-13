#!/usr/bin/env bash
# End-to-end verification of the independently executable host through the
# TypeScript SDK (ADR-0033): run the real Spatial.Host process and drive it
# from Node with the browser-compatible SDK over real HTTP — no Docker.
# Requires: .NET 10, node >= 22.6, and a built solution.
set -euo pipefail
cd "$(dirname "$0")/.."

HOST_LOG="$(mktemp -t spatial-host.XXXXXX.log)"
PID=""

# A free port keeps the e2e from colliding with a developer's host; pin one
# with SPATIAL_E2E_PORT when a fixed port is wanted.
HOST_PORT="${SPATIAL_E2E_PORT:-$(node -e 'const net = require("node:net"); const s = net.createServer(); s.listen(0, "127.0.0.1", () => { process.stdout.write(String(s.address().port)); s.close(); });')}"
HOST_URL="http://127.0.0.1:${HOST_PORT}"

cleanup() {
  if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
    kill "$PID" 2>/dev/null || true
    wait "$PID" 2>/dev/null || true
  fi
  rm -f "$HOST_LOG"
}
trap cleanup EXIT

echo "== build the host =="
dotnet build src/Spatial.Host/Spatial.Host.csproj >/dev/null

echo "== start the host on $HOST_URL =="
# Declared maps so the GeoServices MapServer e2e has services to discover
# and render (the demo store is read-only and always present).
Spatial__Maps__Declared__0__Name=world \
  Spatial__Maps__Declared__0__Store=demo \
  Spatial__Maps__Declared__0__Services__0=Map \
  Spatial__Maps__Declared__0__Layers__0__Dataset=demo.cities \
  Spatial__Maps__Declared__0__Layers__0__LayerId=0 \
  Spatial__Maps__Declared__0__Layers__0__Name=Cities \
  Spatial__Maps__Declared__0__Layers__0__Style='[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":6,"circle-opacity":1.0}}]' \
  Spatial__Maps__Declared__1__Name=rich \
  Spatial__Maps__Declared__1__Store=demo \
  Spatial__Maps__Declared__1__Services__0=Map \
  Spatial__Maps__Declared__1__Layers__0__Dataset=demo.cities \
  Spatial__Maps__Declared__1__Layers__0__LayerId=0 \
  Spatial__Maps__Declared__1__Layers__0__Name=Cities \
  Spatial__Maps__Declared__1__Layers__0__Style='[{"type":"circle","filter":["all",[">=","population",0],["<","population",1000000]],"paint":{"circle-color":"#ffffcc","circle-radius":4}},{"type":"circle","filter":["all",[">=","population",1000000],["<","population",100000000]],"paint":{"circle-color":"#ff0000","circle-radius":8}},{"type":"symbol","layout":{"text-field":["get","name"],"text-size":11},"paint":{"text-color":"#262626"}}]' \
  ASPNETCORE_URLS="$HOST_URL" \
  dotnet run --project src/Spatial.Host --no-build --no-launch-profile --urls "$HOST_URL" >"$HOST_LOG" 2>&1 &
PID=$!

echo "== wait for readiness =="
for _ in $(seq 1 60); do
  if curl -fsS -m 2 "$HOST_URL/health/ready" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$PID" 2>/dev/null; then
    echo "host died during startup:" >&2
    cat "$HOST_LOG" >&2
    exit 1
  fi
  sleep 1
done

curl -fsS "$HOST_URL/health/ready" | grep -q '"status":"ready"' \
  || { echo "host never became ready"; cat "$HOST_LOG" >&2; exit 1; }

echo "== refresh the OpenAPI snapshot and regenerate the SDK types =="
curl -fsS "$HOST_URL/openapi/v1.json" -o clients/typescript/scripts/openapi.snapshot.json
# The host ran on a temporary port; the committed snapshot keeps a stable
# canonical URL so the file does not churn on every run.
sed -i -E 's#http://127\.0\.0\.1:[0-9]+/#http://127.0.0.1:5199/#g' clients/typescript/scripts/openapi.snapshot.json
(
  cd clients/typescript
  npm ci >/dev/null
  npm run generate >/dev/null
)

echo "== run the TypeScript SDK against the live host =="
SPATIAL_HOST_URL="$HOST_URL" npm --prefix clients/typescript run test:e2e

echo "== e2e ok: the host served the TypeScript SDK independently =="
