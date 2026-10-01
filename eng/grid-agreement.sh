#!/usr/bin/env bash
# The published-bundle agreement with PROJ, run as a deployment exercise
# (SpatialEngine-yt2, ADR-0179).
#
# ADR-0105 filed the control points over *published* datum shift bundles rather
# than closing them: every bundle is third-party data whose licence and
# redistribution are open questions, so a test that fetched one would be a test
# that depended on the network, and a gate that fetched one would decide the
# maintainer's licence position for them. This script is the operator side of
# that. It deploys nothing, downloads nothing and installs nothing: it says
# what this machine has, and then runs the suite over it.
#
#   eng/grid-agreement.sh                    # report the state, then run the suite
#   eng/grid-agreement.sh --plan             # print the state and stop
#
# Environment:
#   SPATIALENGINE_GRID_DIR   deployed bundle directories, ':'-delimited in
#                            priority order. The bundle file names the catalogue
#                            publishes are OSTN15_osgb_02_NTv2_OSGBtoETRS.gsb
#                            (OSGB 36) and NAD83_to_WGS84_NTv2.gsb (NAD 83).
#   SPATIALENGINE_PROJ       path to cs2cs when PROJ is installed somewhere the
#                            PATH does not reach. PROJ also needs its *own* copy
#                            of the same bundle in its data directory (PROJ_DATA,
#                            or PROJ_LIB on PROJ 8 and earlier) — it is PROJ that
#                            reads the bundle, and a Helmert from PROJ proves
#                            nothing, so a run without the grid installed there
#                            is refused rather than compared.
set -euo pipefail
cd "$(dirname "$0")/.."

PROJECT="tests/unit/Spatial.Transformations.ProjNet.Tests/Spatial.Transformations.ProjNet.Tests.csproj"
FILTER="FullyQualifiedName~PublishedGrid"
PLAN=0
[[ "${1:-}" == "--plan" ]] && PLAN=1

GRID_DIR="${SPATIALENGINE_GRID_DIR:-}"
CS2CS="${SPATIALENGINE_PROJ:-$(command -v cs2cs || true)}"

echo "published-bundle agreement with PROJ — ADR-0179, amending ADR-0105 §licence"
echo
echo "  SPATIALENGINE_GRID_DIR: ${GRID_DIR:-<unset — no bundle is deployed>}"
if [[ -z "$GRID_DIR" ]]; then
  echo "    a bundle is deployed by dropping the file the catalogue names into a directory and"
  echo "    pointing this at it; which bundles may lawfully be deployed on a machine is that"
  echo "    operator's licence position, and no part of this repository fetches one."
else
  IFS=':' read -r -a dirs <<< "$GRID_DIR"
  for dir in "${dirs[@]}"; do
    if [[ -d "$dir" ]]; then
      echo "    ${dir} exists: $(find "$dir" -maxdepth 1 -type f \( -name '*.gsb' -o -name '*.las' -o -name '*.los' \) -printf '%f ' 2>/dev/null || true)"
    else
      echo "    ${dir} does not exist"
    fi
  done
fi
echo
echo "  PROJ (cs2cs): ${CS2CS:-<not installed — the comparison cannot run>}"
if [[ -z "$CS2CS" ]]; then
  echo "    install PROJ and put the same bundle in its data directory; the engine's reader and"
  echo "    PROJ's are different code and this is what the residual between them measures."
fi
echo

if [[ $PLAN -eq 1 ]]; then
  echo "dry run: would run ${PROJECT} --filter ${FILTER}"
  exit 0
fi

# The suite reports its own state as a case that always runs, so a run in which
# both comparisons skipped is legible rather than green.
dotnet test "$PROJECT" --filter "$FILTER" --logger "console;verbosity=normal" "$@"
