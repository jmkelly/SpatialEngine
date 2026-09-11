#!/usr/bin/env bash
# End-to-end browser verification of the workbench (ADR-0033, plan §18 "web
# tests — run the browser workbench independently using Playwright; Tauri
# must not be needed"): build the workbench, run the real Spatial.Host
# serving the app from Spatial:WebRoot (same origin, no Docker, no Tauri),
# and drive it from Playwright in a real browser.
# Requires: .NET 10, node >= 22.6, and a built solution.
set -euo pipefail
cd "$(dirname "$0")/.."

WORKBENCH_URL="http://127.0.0.1:5999"
PORT="${WORKBENCH_URL##*:}"
WEB_ROOT="$(pwd)/artifacts/workbench-web"
HOST_LOG="$(mktemp -t spatial-workbench-host.XXXXXX.log)"
PID=""

# A zombie host from an interrupted run would answer the e2e with stale
# state — never reuse one.
kill_engine_hosts() {
  pkill -f 'dotnet run --project src/Spatial.Host' 2>/dev/null || true
  pkill -f 'Spatial.Host --urls' 2>/dev/null || true
  pkill -f 'Spatial.Host.dll' 2>/dev/null || true
  for _ in $(seq 1 15); do
    if ! (ss -ltn 2>/dev/null || netstat -ltn 2>/dev/null || true) | grep -q ":$PORT "; then
      return 0
    fi
    sleep 1
  done
  echo "port $PORT is still held by a stale host; refusing to run against it" >&2
  exit 1
}
kill_engine_hosts

cleanup() {
  if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
    kill "$PID" 2>/dev/null || true
    wait "$PID" 2>/dev/null || true
  fi
  # The `dotnet run` wrapper's apphost child serves the port; without this
  # an interrupted run would leave a stale host answering future runs.
  pkill -f 'Spatial.Host --urls' 2>/dev/null || true
  rm -f "$HOST_LOG"
}
trap cleanup EXIT

echo "== build the host and the workbench =="
dotnet build src/Spatial.Host/Spatial.Host.csproj >/dev/null
(
  cd apps/workbench-web
  npm ci >/dev/null
  npm run build >/dev/null
)
rm -rf "$WEB_ROOT"
mkdir -p "$WEB_ROOT"
cp -r apps/workbench-web/dist/* "$WEB_ROOT/"

echo "== start the host serving the workbench =="
Spatial__WebRoot="$WEB_ROOT" \
  ASPNETCORE_URLS="$WORKBENCH_URL" \
  dotnet run --project src/Spatial.Host --no-build --no-launch-profile --urls "$WORKBENCH_URL" >"$HOST_LOG" 2>&1 &
PID=$!

echo "== wait for readiness =="
for _ in $(seq 1 90); do
  if curl -fsS -m 2 "$WORKBENCH_URL/health/ready" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$PID" 2>/dev/null; then
    echo "host died during startup:" >&2
    cat "$HOST_LOG" >&2
    exit 1
  fi
  sleep 1
done

curl -fsS "$WORKBENCH_URL/health/ready" | grep -q '"status":"ready"' \
  || { echo "host never became ready"; cat "$HOST_LOG" >&2; exit 1; }

echo "== the host serves the workbench =="
curl -fsS "$WORKBENCH_URL/" | grep -q 'id="root"' \
  || { echo "the workbench index is not served at the root"; cat "$HOST_LOG" >&2; exit 1; }

echo "== run Playwright in a real browser =="
(
  cd tests/end-to-end-web
  npm ci >/dev/null
  npx playwright install chromium --with-deps >/dev/null 2>&1 || npx playwright install chromium >/dev/null
  WORKBENCH_URL="$WORKBENCH_URL" npx playwright test
)

echo "== browser e2e ok: the workbench runs in a normal browser against the host =="
