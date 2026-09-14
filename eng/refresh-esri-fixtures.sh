#!/usr/bin/env bash
# Live Esri fixture refresh, nightly/explicit (T-065).
#
# Re-fetches a small probe set from the public, unauthenticated Esri services
# the offline parity suites were transcribed/recorded from, and diffs the live
# answers against the checked-in fixtures:
#
#   sampleserver6  -> tests/fixtures/esri-docs/geometryserver/metadata.json
#                     (Esri REST docs + sampleserver6 GeometryServer pattern)
#   services.arcgisonline.com (Canvas basemap)
#                  -> tests/fixtures/arcgis/captured/<basemap>/service.json
#
# The broad captured corpus (tests/fixtures/arcgis/captured/index.json) is
# refreshed through the existing discovery loop in research/arcgis/
# (harvest.py --services-file + slim.py + report.py); this script wraps that
# loop for the refresh services instead of reimplementing it.
#
# Modes:
#   --check (default)  fetch the probes, print the diff summary, change
#                      nothing under tests/. Safe for scheduled dry-runs.
#   --write            additionally refresh the mapped fixture envelopes
#                      from the live bodies and print `git diff --stat`.
#                      Produces a refresh PR; product code is never touched.
#   --force            skip harvest.py's on-disk cache for the corpus pass.
#
# HARD RULES (pinned by EsriRefreshGateTests, default suite stays offline):
#   - This script NEVER runs in eng/verify.sh. The PR gate replays
#     checked-in fixtures only (EsriDocsReplayTests, RealWorldFixtureTests).
#   - Live probes live in EsriLiveRefreshTests and are skipped unless
#     SPATIAL_ESRI_LIVE=1 (nightly/explicit only).
#   - Public endpoints only, no credentials, no secrets in the repo.
#   - Drift the script finds is filed via `eng/tasks add --area interop.esri`
#     (a refresh PR shows only fixture diffs); expectations are never
#     "fixed" by editing product code here, and product behaviour is never
#     changed to match drift.
#
# Environment overrides:
#   SAMPLES_BASE     default https://sampleserver6.arcgisonline.com
#   ARCGISONLINE_BASE default https://services.arcgisonline.com
set -euo pipefail
cd "$(dirname "$0")/.."

MODE="check"
FORCE="0"
for arg in "$@"; do
  case "$arg" in
    --check) MODE="check" ;;
    --write) MODE="write" ;;
    --force) FORCE="1" ;;
    -h|--help)
      sed -n '1,45p' "$0"
      exit 0
      ;;
    *)
      echo "usage: eng/refresh-esri-fixtures.sh [--check] [--write] [--force]" >&2
      exit 2
      ;;
  esac
done

SAMPLES_BASE="${SAMPLES_BASE:-https://sampleserver6.arcgisonline.com}"
ARCGISONLINE_BASE="${ARCGISONLINE_BASE:-https://services.arcgisonline.com}"
TIMEOUT="${REFRESH_TIMEOUT:-20}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

SAME=0
DRIFT=0
UNREACHABLE=0

fetch() { # url, out-body, out-headers
  local url="$1" body="$2" headers="$3"
  curl -fsSL -m "$TIMEOUT" --retry 1 \
    -H "Accept: application/json" \
    -H "User-Agent: SpatialEngine-FixtureRefresh/1.0 (+offline parity suite)" \
    -D "$headers" -o "$body" "$url" 2>/dev/null
}

http_status() { head -n 1 "$1" | awk '{print $2}'; }

# compare_keys <live-body> <fixture> <jq-path> <label>
# Verdict: every key recorded under <jq-path> must still exist in the live
# body (live services grow: currentVersion bumps, new keys). Missing keys or
# an unparseable live body is DRIFT/UNREACHABLE, never a silent pass.
compare_keys() {
  local live="$1" fixture="$2" path="$3" label="$4"
  python3 - "$live" "$fixture" "$path" "$label" <<'EOF'
import json, sys
live_path, fixture_path, jq_path, label = sys.argv[1:5]
try:
    live = json.load(open(live_path, encoding="utf-8"))
except (OSError, json.JSONDecodeError) as exc:
    print(f"[{label}] UNREACHABLE: live body is not JSON ({exc}).")
    sys.exit(3)
if isinstance(live, dict) and "error" in live:
    print(f"[{label}] UNREACHABLE: live service answered an error envelope: {json.dumps(live['error'])[:200]}.")
    sys.exit(3)
node = json.load(open(fixture_path, encoding="utf-8"))
for part in jq_path.strip(".").split("."):
    node = node[part]
if not isinstance(node, dict):
    print(f"[{label}] UNREACHABLE: fixture node {jq_path} is not an object.")
    sys.exit(3)
if not isinstance(live, dict):
    print(f"[{label}] DRIFT: live body is not a JSON object; recorded keys cannot be compared.")
    sys.exit(1)
missing = sorted(k for k in node if k not in live)
extra = sorted(k for k in live if k not in node)
if missing:
    print(f"[{label}] DRIFT: {len(missing)} recorded key(s) missing live: {', '.join(missing)}.")
    if extra:
        print(f"[{label}]        live also adds {len(extra)} new key(s): {', '.join(extra[:8])}{'...' if len(extra) > 8 else ''}.")
    sys.exit(1)
print(f"[{label}] SAME: all {len(node)} recorded key(s) present live.", end="")
if extra:
    print(f" Live adds {len(extra)} new key(s): {', '.join(extra[:8])}{'...' if len(extra) > 8 else ''}.", end="")
print()
EOF
}

probe() { # id, url, fixture, jq-path
  local id="$1" url="$2" fixture="$3" path="$4"
  local body="$WORK/$id.body" headers="$WORK/$id.headers"
  echo "== probe [$id] =="
  echo "live:    $url"
  echo "fixture: $fixture"
  if ! fetch "$url" "$body" "$headers"; then
    echo "[$id] UNREACHABLE: fetch failed (service down, blocked network, or timeout ${TIMEOUT}s)."
    UNREACHABLE=$((UNREACHABLE + 1))
    return 0
  fi
  local status
  status="$(http_status "$headers")"
  if [[ "$status" != "200" ]]; then
    echo "[$id] UNREACHABLE: HTTP $status from the live service."
    UNREACHABLE=$((UNREACHABLE + 1))
    return 0
  fi
  local verdict
  if compare_keys "$body" "$fixture" "$path" "$id"; then
    verdict=0
  else
    verdict=$?
  fi
  case "$verdict" in
    0) SAME=$((SAME + 1)) ;;
    1) DRIFT=$((DRIFT + 1)) ;;
    *) UNREACHABLE=$((UNREACHABLE + 1)) ;;
  esac
  return 0
}

GEOMETRY_ROOT_URL="$SAMPLES_BASE/arcgis/rest/services/Utilities/Geometry/GeometryServer?f=json"
GEOMETRY_PROJECT_URL="$SAMPLES_BASE/arcgis/rest/services/Utilities/Geometry/GeometryServer/project?geometries=%7B%22geometryType%22%3A%22esriGeometryPoint%22%2C%22geometries%22%3A%5B%7B%22x%22%3A-117%2C%22y%22%3A34%7D%5D%7D&inSR=4326&outSR=3857&f=json"
BASEMAP_URL="$ARCGISONLINE_BASE/arcgis/rest/services/Canvas/World_Dark_Gray_Base/MapServer?f=json"

METADATA_FIXTURE="tests/fixtures/esri-docs/geometryserver/metadata.json"
PROJECT_FIXTURE="tests/fixtures/esri-docs/geometryserver/project-point.json"
BASEMAP_FIXTURE="tests/fixtures/arcgis/captured/canvas-world-dark-gray-base-mapserver/service.json"

echo "Esri fixture refresh ($MODE): $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo

probe "geometry-root" "$GEOMETRY_ROOT_URL" "$METADATA_FIXTURE" ".esriResponse"

# The project probe carries no refreshable envelope (project-point.json is a
# doc-transcribed replay, not a live recording), so it is a shape check only:
# the live GeometryServer must still answer a `geometries` array.
echo "== probe [geometry-project] =="
echo "live:    $GEOMETRY_PROJECT_URL"
echo "fixture: $PROJECT_FIXTURE (shape check only, not refreshed)"
pbody="$WORK/geometry-project.body"
pheaders="$WORK/geometry-project.headers"
if ! fetch "$GEOMETRY_PROJECT_URL" "$pbody" "$pheaders" || [[ "$(http_status "$pheaders")" != "200" ]]; then
  echo "[geometry-project] UNREACHABLE: fetch failed or non-200 from the live service."
  UNREACHABLE=$((UNREACHABLE + 1))
elif python3 -c "import json,sys; b=json.load(open('$pbody')); assert isinstance(b.get('geometries'), list) and b['geometries'], 'no geometries array'" 2>/dev/null; then
  echo "[geometry-project] SAME: live answer still carries a non-empty \`geometries\` array."
  SAME=$((SAME + 1))
else
  echo "[geometry-project] DRIFT: live answer lost the \`geometries\` array shape."
  head -c 300 "$pbody"; echo
  DRIFT=$((DRIFT + 1))
fi

probe "basemap-root" "$BASEMAP_URL" "$BASEMAP_FIXTURE" ".body"

if [[ "$MODE" == "write" ]]; then
  echo
  echo "== refresh --write =="
  if [[ "$DRIFT" != "0" || "$UNREACHABLE" != "0" ]]; then
    echo "Live sources drifted or are unreachable; refreshing envelopes from"
    echo "what is reachable now would enshrine a bad snapshot. Re-run --check"
    echo "when the probes are green, then re-run --write."
  else
    python3 - "$WORK/geometry-root.body" "$METADATA_FIXTURE" <<'EOF'
import json, sys
live = json.load(open(sys.argv[1], encoding="utf-8"))
path = sys.argv[2]
fixture = json.load(open(path, encoding="utf-8"))
fixture["esriResponse"] = live
json.dump(fixture, open(path, "w", encoding="utf-8"), indent=2, sort_keys=True)
open(path, "a", encoding="utf-8").write("\n")
print(f"updated esriResponse in {path} from the live GeometryServer root.")
EOF
    services_file="$WORK/refresh-services.txt"
    printf '%s\n' "$ARCGISONLINE_BASE/arcgis/rest/services/Canvas/World_Dark_Gray_Base/MapServer" >"$services_file"
    harvest_args=(--services-file "$services_file" --max-services 4)
    [[ "$FORCE" == "1" ]] && harvest_args+=(--force)
    python3 research/arcgis/harvest.py "${harvest_args[@]}"
    python3 research/arcgis/slim.py
    python3 research/arcgis/report.py | tail -n 20
    echo
    git diff --stat -- tests/fixtures || true
  fi
fi

echo
echo "== diff summary =="
echo "SAME: $SAME  DRIFT: $DRIFT  UNREACHABLE: $UNREACHABLE  (mode: $MODE)"
if [[ "$DRIFT" != "0" ]]; then
  echo "Drift found: file it, do not fix product code here, e.g."
  echo "  eng/tasks add \"Esri fixture drift: <probe> <missing keys>\" --area interop.esri --priority 3"
fi
echo "PR gate remains offline: eng/verify.sh never invokes this script"
echo "(pinned by EsriRefreshGateTests.Refresh_script_is_not_part_of_the_default_gate)."
