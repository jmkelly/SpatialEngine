#!/usr/bin/env bash
# Seed a realistic, non-trivial spatial dataset and a set of styled feature
# and map services into a Spatial Engine host.
#
# It is a thin wrapper around `node tools/seed/seed.mjs` (see that file and
# `tools/seed/manifest.mjs` for what is fetched and published):
#   * if `SPATIAL_SEED_HOST` (or the default URL) is already serving, it seeds
#     that host and leaves it alone;
#   * otherwise it starts a host with an admin token and an isolated
#     maps file, seeds it, and leaves it running so the `memory` store
#     stays alive and the workbench can be pointed at it.
#
# Requires: .NET 10 and Node >= 22.6 (the repository baseline).
#
# Environment:
#   SPATIAL_SEED_HOST         host URL (default http://127.0.0.1:5201)
#   SPATIAL_ADMIN_TOKEN       admin token (default seed-admin-token)
#   SPATIAL_SEED_STORE        target store (default memory)
#   SPATIAL_SEED_MAPS         map file for a started host
#
# Any remaining arguments are forwarded to seed.mjs, for example:
#   eng/seed.sh --only=WorldReference --force
set -euo pipefail
cd "$(dirname "$0")/.."

HOST_URL="${SPATIAL_SEED_HOST:-http://127.0.0.1:5201}"
TOKEN="${SPATIAL_ADMIN_TOKEN:-seed-admin-token}"
STORE="${SPATIAL_SEED_STORE:-memory}"
MAPS_PATH="${SPATIAL_SEED_MAPS:-./data/seed-maps.json}"
HOST_DLL="src/Spatial.Host/bin/Debug/net10.0/Spatial.Host.dll"
HOST_LOG="./data/seed-host.log"
HOST_PID="./data/seed-host.pid"
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

echo "== seeding $HOST_URL (store $STORE) =="
node tools/seed/seed.mjs --host="$HOST_URL" --token="$TOKEN" --store="$STORE" "$@"

if [[ "$STARTED" == "1" ]]; then
  echo ""
  echo "The host is still running at $HOST_URL (pid $(cat "$HOST_PID"))."
  echo "Stop it with: kill \$(cat $HOST_PID)"
fi
