#!/usr/bin/env bash
# Visual conformance harness, slice A (T-066): one command brings up the real
# Spatial.Host with seed data for visual parity work.
#
# It builds the host, starts it when none is running (pattern: eng/e2e-web.sh,
# lifecycle: eng/seed.sh), loads the `seed.mjs` dataset, and wires the `qgis`
# WMS map (demo points + memory lines/polygons) that the later conformance
# slices and T-068 share. Later slices add their own pages and toggles on top
# of this host; this script grows no product surface beyond scaffolding.
#
# Requires: .NET 10 and Node >= 22.6 (the repository baseline). Fetching the
# seed datasets requires network access; the qgis map itself is local fixture
# data and needs none.
#
# Environment:
#   SPATIAL_CONFORMANCE_PORT    host port (default 5251)
#   SPATIAL_CONFORMANCE_HOST    host URL (default http://127.0.0.1:<port>)
#   SPATIAL_CONFORMANCE_TOKEN   admin token (default conformance-admin-token)
#   SPATIAL_CONFORMANCE_MAPS    map file for a started host
#
# Any remaining arguments are forwarded to seed.mjs, for example:
#   eng/conformance.sh --only=WorldReference --force
set -euo pipefail
cd "$(dirname "$0")/.."

PORT="${SPATIAL_CONFORMANCE_PORT:-5251}"
HOST_URL="${SPATIAL_CONFORMANCE_HOST:-http://127.0.0.1:${PORT}}"
TOKEN="${SPATIAL_CONFORMANCE_TOKEN:-conformance-admin-token}"
MAPS_PATH="${SPATIAL_CONFORMANCE_MAPS:-./data/conformance-maps.json}"
HOST_DLL="src/Spatial.Host/bin/Debug/net10.0/Spatial.Host.dll"
HOST_LOG="./data/conformance-host.log"
HOST_PID="./data/conformance-host.pid"
STARTED=0

is_ready() { curl -fsS -m 2 "$HOST_URL/health/ready" >/dev/null 2>&1; }

if is_ready; then
  echo "== using the host already running at $HOST_URL =="
else
  echo "== building and starting a host at $HOST_URL =="
  dotnet build src/Spatial.Host/Spatial.Host.csproj >/dev/null
  mkdir -p ./data
  SPATIAL_ADMIN_TOKEN="$TOKEN" \
  Spatial__Maps__Path="$MAPS_PATH" \
    nohup dotnet "$HOST_DLL" --urls "$HOST_URL" >"$HOST_LOG" 2>&1 &
  echo $! >"$HOST_PID"
  STARTED=1

  for _ in $(seq 1 60); do
    is_ready && break
    sleep 1
  done
  if ! is_ready; then
    echo "the host never became ready; log follows:" >&2
    cat "$HOST_LOG" >&2
    exit 1
  fi
fi

echo "== seeding $HOST_URL (seed.sh data) =="
node tools/seed/seed.mjs --host="$HOST_URL" --token="$TOKEN" "$@"

echo "== wiring the qgis conformance map (demo points + memory lines/polygons) =="
ARGS=(--host="$HOST_URL" --token="$TOKEN")
for arg in "$@"; do
  case "$arg" in
    --force|--dry-run) ARGS+=("$arg") ;;
  esac
done
node tools/seed/qgis-map.mjs "${ARGS[@]}"

echo ""
echo "Conformance host ready at $HOST_URL:"
echo "  GeoServices catalog: $HOST_URL/arcgis/rest/services?f=json"
echo "  Maps:                $HOST_URL/api/maps"
echo "  QGIS WMS:            $HOST_URL/ogc/qgis/wms?SERVICE=WMS&REQUEST=GetCapabilities"
if [[ "$STARTED" == "1" ]]; then
  echo ""
  echo "The host is still running at $HOST_URL (pid $(cat "$HOST_PID"))."
  echo "Stop it with: kill \$(cat $HOST_PID)"
fi
