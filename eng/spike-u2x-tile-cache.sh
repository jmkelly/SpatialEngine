#!/usr/bin/env bash
# SpatialEngine-u2x.21.2 measurement spike: per-layer tile composition.
#
# Answers the question SpatialEngine-u2x.21 deliberately left open — is it
# worth composing tiles per layer, or is the whole-map flush (ADR-0046 +
# ADR-0083) the right granularity to keep?
#
# It publishes a five-layer city basemap into a memory store through the
# store's own create/write faces, renders the tile working set a client pulls
# for one view, and measures:
#
#   * how many warm cache entries a single-layer data edit throws out, against
#     how many tiles' pixels actually changed (a byte comparison, not an
#     inference);
#   * the same for a single-layer style save;
#   * what a re-render of the invalidated tiles costs;
#   * what a per-layer design would add to every served tile (the composite),
#     bracketed by a pessimistic and an optimistic cache representation;
#   * what each design holds in entries and bytes.
#
# Arm A (whole-map entries) is the shipped design and is measured through the
# real renderer, tiling scheme, store and version fold. Arms B and C are
# EMULATIONS of designs that are not shipped; they are labelled as such in the
# output and no product code is changed by this spike.
#
#   eng/spike-u2x-tile-cache.sh                       # in-memory store, no Docker
#   eng/spike-u2x-tile-cache.sh --repeats=4          # 4 runs, to see the noise
#   eng/spike-u2x-tile-cache.sh --host=URL           # also check the mirror
#
# Any other arguments are forwarded to the harness (--zoom, --layers, --maps,
# --density, --iterations, --warmup, --label, --max-entries, --max-bytes).
#
# The measurement box is shared with a parallel agent swarm, so absolute
# wall times move by 2x or more between runs. The counts and byte figures do
# not. Read eng/spike-u2x-tile-cache/RESULTS.md, not the raw output, for the
# decision.
set -euo pipefail
cd "$(dirname "$0")/.."

HARNESS="eng/spike-u2x-tile-cache/Spatial.Spike.TileCache.csproj"
ARTIFACTS="artifacts/spike-u2x.21.2"
REPEATS=1
FORWARD=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repeats=*) REPEATS="${1#*=}" ;;
    --host=* | --zoom=* | --layers=* | --maps=* | --density=* | --iterations=* \
      | --warmup=* | --label=* | --max-entries=* | --max-bytes=*)
      FORWARD+=("$1")
      ;;
    -h | --help)
      sed -n '2,35p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) FORWARD+=("$1") ;;
  esac
  shift
done

mkdir -p "$ARTIFACTS"

# Warm the build once so the first timed run is not also the first compile.
dotnet build -c Release "$HARNESS" --nologo -v quiet >/dev/null

for run in $(seq 1 "$REPEATS"); do
  echo "===== run $run of $REPEATS ====="
  dotnet run -c Release --no-build --project "$HARNESS" -- --label="run$run" "${FORWARD[@]}" \
    | tee "$ARTIFACTS/run$run.txt"
  echo ""
done

echo "Artifacts in $ARTIFACTS/"
