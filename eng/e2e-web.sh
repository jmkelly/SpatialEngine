#!/usr/bin/env bash
# End-to-end verification of the independently executable host through the
# TypeScript SDK (plan §16 Phase 9): pack the NetTopologySuite worker, run the
# real Spatial.Host process against it, and drive it from Node with the
# browser-compatible SDK over real HTTP — no Docker, no Tauri.
# Requires: .NET 10, node >= 22.6, and a built solution.
set -euo pipefail
cd "$(dirname "$0")/.."

HOST_URL="http://127.0.0.1:5199"
PACKAGES_ROOT="${PACKAGES_ROOT:-$(pwd)/artifacts/plugins}"
HOST_LOG="$(mktemp -t spatial-host.XXXXXX.log)"
PID=""

cleanup() {
  if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
    kill "$PID" 2>/dev/null || true
    wait "$PID" 2>/dev/null || true
  fi
  rm -f "$HOST_LOG"
}
trap cleanup EXIT

echo "== build the host and pack the NTS worker package =="
dotnet build src/Spatial.Host/Spatial.Host.csproj >/dev/null
rm -rf "$PACKAGES_ROOT"
dotnet run --project eng/tools/PluginPacker -- --packages-root "$PACKAGES_ROOT" --only nts

echo "== start the host =="
Spatial__PackagesRoot="$PACKAGES_ROOT" \
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
(
  cd clients/typescript
  npm ci >/dev/null
  npm run generate >/dev/null
)

echo "== run the TypeScript SDK against the live host =="
SPATIAL_HOST_URL="$HOST_URL" npm --prefix clients/typescript run test:e2e

echo "== e2e ok: the host served the TypeScript SDK independently =="