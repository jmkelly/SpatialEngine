#!/usr/bin/env bash
# End-to-end verification of the Spatial CLI against a real, independently
# executable host (ADR-0052): build the host and the CLI, run the host with an
# admin token, then drive the CLI over real HTTP — ingest a small file,
# compose a styled map over it, and curl the MapServer it projects. No Docker,
# modeled on eng/e2e-web.sh.
#
# Requires: .NET 10, node >= 22.6, and the CLI built for its own project.
set -euo pipefail
cd "$(dirname "$0")/.."

HOST_LOG="$(mktemp -t spatial-cli-host.XXXXXX.log)"
GEOJSON="$(mktemp -t spatial-cli.XXXXXX.geojson)"
MAPS_DIR="$(mktemp -d -t spatial-cli-maps.XXXXXX)"
MAPS_FILE="$MAPS_DIR/maps.json"
PID=""
TOKEN="cli-e2e-token"

# A free port keeps the e2e from colliding with a developer's host; pin one
# with SPATIAL_E2E_PORT when a fixed port is wanted.
HOST_PORT="${SPATIAL_E2E_PORT:-$(node -e 'const net = require("node:net"); const s = net.createServer(); s.listen(0, "127.0.0.1", () => { process.stdout.write(String(s.address().port)); s.close(); });')}"
HOST_URL="http://127.0.0.1:${HOST_PORT}"

cleanup() {
  if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
    kill "$PID" 2>/dev/null || true
    wait "$PID" 2>/dev/null || true
  fi
  rm -f "$HOST_LOG" "$GEOJSON"
  rm -rf "$MAPS_DIR"
}
trap cleanup EXIT

# Every CLI call targets the throwaway host; mutations also pass the token.
cli() {
  dotnet run --project clients/dotnet/Spatial.Cli --no-build -- --host "$HOST_URL" "$@"
}

echo "== build the host and the CLI =="
dotnet build src/Spatial.Host/Spatial.Host.csproj >/dev/null
dotnet build clients/dotnet/Spatial.Cli/Spatial.Cli.csproj >/dev/null

echo "== start the host on $HOST_URL =="
SPATIAL_ADMIN_TOKEN="$TOKEN" Spatial__Maps__Path="$MAPS_FILE" ASPNETCORE_URLS="$HOST_URL" \
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

echo "== dataset add: write and ingest a two-point GeoJSON =="
cat >"$GEOJSON" <<'JSON'
{"type":"FeatureCollection","features":[
  {"type":"Feature","geometry":{"type":"Point","coordinates":[0,0]},"properties":{"name":"origin"}},
  {"type":"Feature","geometry":{"type":"Point","coordinates":[10,10]},"properties":{"name":"ten-ten"}}
]}
JSON
cli dataset add --file "$GEOJSON" --dataset public.cli_e2e --srid 4326 \
  --identity auto --store memory --token "$TOKEN"

echo "== map create: publish a MapServer over the dataset =="
cli map create --name CliE2E --kind map --store memory \
  --layer 'public.cli_e2e=Cli E2E' --token "$TOKEN"

echo "== map set-style: draw the points red =="
cli map set-style --map CliE2E --dataset public.cli_e2e \
  --geometry point --color '#ff0000' --token "$TOKEN"

echo "== map export: prove the MapServer is served =="
ENDPOINT="$(cli map export CliE2E --format url)"
echo "  MapServer: $ENDPOINT"
curl -fsS "$ENDPOINT?f=json" | grep -q '"mapName"' \
  || { echo "the exported MapServer did not respond"; exit 1; }

echo "== cli e2e ok: the CLI ingested a file and served a styled MapServer =="
