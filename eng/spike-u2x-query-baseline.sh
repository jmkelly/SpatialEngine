#!/usr/bin/env bash
# SpatialEngine-u2x.1 measurement spike: the feature-query baseline.
#
# Runs the harness in eng/spike-u2x-query-baseline over the 34,135-row
# world-cities layer and prints wall time, rows materialised into
# FeatureBatch pages and managed allocation for each path. Nothing in the
# product is changed; this only produces the number the Tier 1 branch of
# SpatialEngine-u2x is justified by.
#
#   eng/spike-u2x-query-baseline.sh                     # in-memory store, no Docker
#   eng/spike-u2x-query-baseline.sh --store=postgis     # real PostGIS in a container
#   eng/spike-u2x-query-baseline.sh --host=URL --host-service=NAME --host-layer=NAME
#
# For the end-to-end run on a host that serves PostGIS, use
# eng/spike-u2x-postgis-e2e.sh: it starts the database, the host and the
# publish flow for you.
#
# With --store=postgis a throwaway postgis/postgis container is started,
# the snapshot is loaded, the paths are measured twice — once over the table
# as the store created it (no indexes) and once after the GiST and attribute
# indexes a real deployment would have — and the plans PostgreSQL chose are
# printed. The container is removed on exit.
#
# Any other arguments are forwarded to the harness (--iterations, --warmup,
# --label, --json).
set -euo pipefail
cd "$(dirname "$0")/.."

HARNESS="eng/spike-u2x-query-baseline/Spatial.Spike.QueryBaseline.csproj"
IMAGE="postgis/postgis:16-3.4"
CONTAINER="spatial-spike-u2x1"
DB="spatial"
ARTIFACTS="artifacts/spike-u2x.1"
STORE="memory"
FORWARD=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --store=*) STORE="${1#*=}" ;;
    --reuse)
      FORWARD+=("--reuse")
      ;;
    --host=* | --host-service=* | --host-layer=* | --iterations=* | --warmup=* | --label=* | --json=* | --connection=*)
      FORWARD+=("$1")
      ;;
    -h | --help)
      sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) FORWARD+=("$1") ;;
  esac
  shift
done

mkdir -p "$ARTIFACTS"

psql_in() {
  docker exec -i "$CONTAINER" psql -v ON_ERROR_STOP=1 -U spatial -d "$DB" "$@"
}

stop_container() {
  if [[ "${SKIP_TEARDOWN:-0}" != "1" ]]; then
    docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  fi
}

if [[ "$STORE" != "postgis" ]]; then
  dotnet run -c Release --project "$HARNESS" -- --store="$STORE" "${FORWARD[@]}" \
    | tee "$ARTIFACTS/$STORE.txt"
  exit 0
fi

CONNECTION="Host=127.0.0.1;Port=55432;Username=spatial;Password=spatial;Database=$DB"
trap stop_container EXIT

echo "== starting $IMAGE on 127.0.0.1:55432 =="
docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
docker run -d --name "$CONTAINER" \
  -e POSTGRES_USER=spatial -e POSTGRES_PASSWORD=spatial -e POSTGRES_DB="$DB" \
  -p 55432:5432 "$IMAGE" >/dev/null

for _ in $(seq 1 60); do
  if docker exec "$CONTAINER" psql -U spatial -d "$DB" -c "SELECT 1;" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
docker exec "$CONTAINER" psql -U spatial -d "$DB" -c "SELECT 1;" >/dev/null
# The first connections after the database is up can still be refused while
# the entrypoint finishes its own setup, so give the server a moment to settle
# before the harness opens its pool.
sleep 3

echo "== loading the world-cities snapshot (this is the harness's own load) =="
psql_in -c "DROP TABLE IF EXISTS public.spike_world_cities CASCADE;" >/dev/null

echo "== path numbers over the table exactly as the store created it (no indexes) =="
dotnet run -c Release --project "$HARNESS" -- \
  --store=postgis --connection="$CONNECTION" --label="postgis/no-index" \
  --json="$ARTIFACTS/postgis-no-index.json" "${FORWARD[@]}" | tee "$ARTIFACTS/postgis-no-index.txt"

echo ""
echo "== the plans PostgreSQL chose =="
psql_in -c "EXPLAIN (COSTS OFF) SELECT * FROM public.spike_world_cities;" | tee "$ARTIFACTS/postgis-no-index-plan.txt"
psql_in -c "EXPLAIN (COSTS OFF) SELECT * FROM public.spike_world_cities WHERE population > 100000 AND ST_Intersects(geometry, ST_MakeEnvelope(-10,36,5,55,4326));"

echo ""
echo "== adding the indexes a real deployment would have (GiST + attribute btree) =="
psql_in <<'SQL' | tee "$ARTIFACTS/postgis-index-ddl.txt"
CREATE INDEX world_cities_geometry_gix ON public.spike_world_cities USING GIST (geometry);
CREATE INDEX world_cities_population_ix ON public.spike_world_cities (population);
CREATE INDEX world_cities_country_ix ON public.spike_world_cities (country);
ANALYZE public.spike_world_cities;
SQL

echo "== path numbers over the indexed table =="
dotnet run -c Release --project "$HARNESS" -- \
  --store=postgis --connection="$CONNECTION" --label="postgis/indexed" --reuse \
  --json="$ARTIFACTS/postgis-indexed.json" "${FORWARD[@]}" | tee "$ARTIFACTS/postgis-indexed.txt"

echo ""
echo "== the plans PostgreSQL chose once the indexes exist =="
psql_in -c "EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF, SUMMARY OFF) SELECT * FROM public.spike_world_cities WHERE population > 100000 AND ST_Intersects(geometry, ST_MakeEnvelope(-10,36,5,55,4326));" \
  | tee "$ARTIFACTS/postgis-indexed-plan.txt"

echo ""
echo "Artifacts in $ARTIFACTS/"
