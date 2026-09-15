#!/usr/bin/env bash
# Spatial Engine environment doctor — non-destructive.
#
# Checks the pinned toolchain, the state of the working tree, and prints the
# task routing table so a change starts in the right project. Reads only; it
# never builds, formats or starts a host.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null || true)"
[[ -n "$ROOT" ]] || ROOT="$(cd "$SCRIPT_DIR/../../../.." && pwd)"
cd "$ROOT"

ok=0
warn=0

check() { # check <label> <actual> <expected-substring>
  local label="$1" actual="$2" want="$3"
  if [[ "$actual" == *"$want"* ]]; then
    printf '  ok    %-22s %s\n' "$label" "$actual"
    ok=$((ok + 1))
  else
    printf '  WARN  %-22s %s (expected %s)\n' "$label" "${actual:-<none>}" "$want"
    warn=$((warn + 1))
  fi
}

echo "== toolchain =="
# global.json pins 10.0.400 with rollForward latestFeature, so any 10.0.4xx
# SDK (and later 10.0 feature bands) is acceptable — check the band, not the
# exact per-machine patch.
if command -v dotnet >/dev/null 2>&1; then
  sdk="$(dotnet --version 2>/dev/null)"
  IFS='.' read -r major minor third <<<"$sdk"
  if [[ "$major.$minor" == "10.0" && "${third:-0}" -ge 400 ]]; then
    printf '  ok    %-22s %s\n' ".NET SDK" "$sdk"
    ok=$((ok + 1))
  else
    printf '  WARN  %-22s %s (global.json pins 10.0.4xx)\n' ".NET SDK" "${sdk:-<none>}"
    warn=$((warn + 1))
  fi
else
  printf '  WARN  %-22s %s\n' ".NET SDK" "dotnet not on PATH"; warn=$((warn + 1))
fi
if command -v node >/dev/null 2>&1; then
  printf '  ok    %-22s %s\n' "node" "$(node --version)"
  ok=$((ok + 1))
else
  printf '  WARN  %-22s %s\n' "node" "not on PATH (needed for web e2e only)"; warn=$((warn + 1))
fi
if command -v git >/dev/null 2>&1; then
  check "git repo" "$(git rev-parse --is-inside-work-tree 2>/dev/null)" "true"
else
  printf '  WARN  %-22s %s\n' "git" "not on PATH"; warn=$((warn + 1))
fi

echo "== working tree =="
dirty="$(git status --porcelain 2>/dev/null | wc -l | tr -d ' ')"
printf '  %-8s %s\n' "changes" "${dirty} path(s) modified"
printf '  %-8s %s\n' "branch" "$(git branch --show-current 2>/dev/null || echo '<detached>')"

echo "== required files =="
for f in SpatialEngine.slnx Directory.Packages.props global.json AGENTS.md eng/verify.sh; do
  [[ -e "$f" ]] && printf '  ok    %s\n' "$f" || { printf '  WARN  %s missing\n' "$f"; warn=$((warn + 1)); }
done

cat <<'GUIDE'

== route your task ==
  core geometry / feature types / codecs  -> Spatial.Core        docs: architecture/distilled/core.md
  service contract (a verb)               -> Spatial.Contracts   docs: architecture/distilled/contracts.md
  algorithm / store / renderer            -> implementation proj  docs: architecture/distilled/plugins.md
  HTTP API, config, SDKs, workbench       -> Spatial.Host        docs: architecture/distilled/host-and-clients.md
  CLI / project file                      -> clients/dotnet       docs: architecture/distilled/cli.md
  ADRs win on conflict                    -> architecture/decisions/

== before done ==
  eng/verify.sh                 # format + build + tests
  eng/cli-e2e.sh                # if the HTTP surface or CLI changed
  quality-loop skill            # CRAP < 10, branch coverage >= 70%
GUIDE

if (( warn > 0 )); then
  echo
  echo "doctor: ${ok} ok, ${warn} warning(s) — resolve before running eng/verify.sh"
  exit 1
fi
echo
echo "doctor: all checks ok"
