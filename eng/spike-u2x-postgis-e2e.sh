#!/usr/bin/env bash
# SpatialEngine-58d: the end-to-end half of the u2x.1 baseline, measured on the
# store half's real deployment path.
#
# eng/spike-u2x-query-baseline.sh --store=postgis measures the store faces
# in-process, and the 2026-09-28 end-to-end numbers were a Debug host on the
# in-memory demo store. This runs the same requests over HTTP against a host
# that serves the PostGIS store: a throwaway postgis/postgis container, the
# committed world-cities snapshot loaded into it through the store's own write
# face, the indexes a deployment would have, a host whose `postgis` store is
# that database, and the dataset published through the admin publish flow
# (PUT /api/maps) as a FeatureServer layer — so the store half and the facade
# half are measured on one path.
#
#   eng/spike-u2x-postgis-e2e.sh                     # whole thing, 20 iterations
#   eng/spike-u2x-postgis-e2e.sh --iterations=30     # extra args go to the harness
#
# Environment:
#   SPIKE_E2E_PORT       container port (default 55433)
#   SPIKE_E2E_HOST_URL   host URL (default http://127.0.0.1:5211)
#   SPIKE_E2E_ADMIN      admin token (default spike-admin-token)
#   SKIP_TEARDOWN=1      leave the container running for a follow-up run
set -euo pipefail
cd "$(dirname "$0")/.."

HARNESS="eng/spike-u2x-query-baseline/Spatial.Spike.QueryBaseline.csproj"
IMAGE="postgis/postgis:16-3.4"
CONTAINER="spatial-spike-u2x-e2e"
DB="spatial"
PORT="${SPIKE_E2E_PORT:-55433}"
HOST_URL="${SPIKE_E2E_HOST_URL:-http://127.0.0.1:5211}"
TOKEN="${SPIKE_E2E_ADMIN:-spike-admin-token}"
SERVICE="spike"
LAYER="world_cities"
MAPS_PATH="./artifacts/spike-u2x.1/e2e-maps.json"
HOST_LOG="./artifacts/spike-u2x.1/e2e-host.log"
ARTIFACTS="artifacts/spike-u2x.1"
CONNECTION="Host=127.0.0.1;Port=$PORT;Username=spatial;Password=spatial;Database=$DB"
FORWARD=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --iterations=* | --warmup=* | --label=* | --json=* | --reuse)
      FORWARD+=("$1")
      ;;
    -h | --help)
      sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) FORWARD+=("$1") ;;
  esac
  shift
done

mkdir -p "$ARTIFACTS"
rm -f "$MAPS_PATH" "$HOST_LOG"

psql_in() { docker exec -i "$CONTAINER" psql -v ON_ERROR_STOP=1 -U spatial -d "$DB" "$@"; }
stop_container() { [[ "${SKIP_TEARDOWN:-0}" == "1" ]] || docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; }

echo "== starting $IMAGE on 127.0.0.1:$PORT =="
docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
docker run -d --name "$CONTAINER" \
  -e POSTGRES_USER=spatial -e POSTGRES_PASSWORD=spatial -e POSTGRES_DB="$DB" \
  -p "$PORT:5432" "$IMAGE" >/dev/null
trap stop_container EXIT

for _ in $(seq 1 60); do
  docker exec "$CONTAINER" psql -U spatial -d "$DB" -c "SELECT 1;" >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$CONTAINER" psql -U spatial -d "$DB" -c "SELECT 1;" >/dev/null
sleep 3

echo "== loading the world-cities snapshot through the store's own write face =="
dotnet run -c Release --project "$HARNESS" -- \
  --store=postgis --connection="$CONNECTION" --label="e2e/load" --iterations=1 --warmup=0 \
  >"$ARTIFACTS/e2e-load.txt"

echo "== the indexes a deployment would have (GiST + attribute btree) =="
psql_in <<'SQL' | tee "$ARTIFACTS/e2e-index-ddl.txt"
CREATE INDEX world_cities_geometry_gix ON public.spike_world_cities USING GIST (geometry);
CREATE INDEX world_cities_population_ix ON public.spike_world_cities (population);
CREATE INDEX world_cities_country_ix ON public.spike_world_cities (country);
ANALYZE public.spike_world_cities;
SQL

echo "== building and starting a host whose 'postgis' store is that database =="
dotnet build src/Spatial.Host/Spatial.Host.csproj >/dev/null
SPATIAL_ADMIN_TOKEN="$TOKEN" \
Spatial__Postgis__ConnectionString="$CONNECTION" \
Spatial__Maps__Path="$MAPS_PATH" \
  nohup dotnet src/Spatial.Host/bin/Debug/net10.0/Spatial.Host.dll --urls "$HOST_URL" >"$HOST_LOG" 2>&1 &
HOST_PID=$!
stop_host() { kill "$HOST_PID" >/dev/null 2>&1 || true; }
trap 'stop_host; stop_container' EXIT

for _ in $(seq 1 60); do
  curl -fsS -m 2 "$HOST_URL/health/ready" >/dev/null 2>&1 && break
  sleep 1
done
if ! curl -fsS -m 2 "$HOST_URL/health/ready" >/dev/null; then
  echo "the host never became ready; log follows:" >&2
  cat "$HOST_LOG" >&2
  exit 1
fi

echo "== publishing the spike dataset through the admin publish flow as '$SERVICE' =="
curl -fsS -X PUT "$HOST_URL/api/maps/$SERVICE" \
  -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
  -d "{\"store\":\"postgis\",\"services\":[\"feature\"],\"layers\":[{\"dataset\":\"public.spike_world_cities\",\"layerId\":0,\"name\":\"$LAYER\"}]}" \
  | tee "$ARTIFACTS/e2e-publish.json"
echo

echo "== the end-to-end numbers, same requests, the PostGIS host =="
dotnet run -c Release --project "$HARNESS" -- \
  --store=postgis --connection="$CONNECTION" --reuse --host="$HOST_URL" \
  --host-service="$SERVICE" --host-layer="$LAYER" --label="e2e/postgis-host" \
  "${FORWARD[@]}" | tee "$ARTIFACTS/e2e-postgis-host.txt"

echo ""
echo "Artifacts in $ARTIFACTS/ (the host log is $HOST_LOG)"
