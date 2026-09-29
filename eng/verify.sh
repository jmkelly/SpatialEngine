#!/usr/bin/env bash
# Verification, in three lanes. Every one of them is `eng/verify.sh`.
#
#   eng/verify.sh            the build gate — the default, and what an agent
#                            runs on every iteration. Builds the solution and
#                            runs the test projects that reach what this branch
#                            changed, plus Spatial.Architecture.Tests. Minutes,
#                            not a quarter of an hour.
#   eng/verify.sh --format   the format lane — `dotnet format
#                            --verify-no-changes` over the projects that own the
#                            changed files (~45 s each, against ~700 s for the
#                            whole solution). A pre-handoff step.
#   eng/verify.sh --full     everything: format over the whole solution, build,
#                            every test project, the python tooling tests. This
#                            is the gate CI runs on every pull request and on
#                            main after the merge; run it locally when a change
#                            genuinely needs the whole thing before it goes
#                            near a PR.
#   eng/verify.sh --plan     print the steps a lane would run, and run nothing
#
# The split exists because the flat gate costs ~25 minutes warm on a 12-core
# box and agents run it constantly. Almost all of that is overhead rather than
# signal: `dotnet format` over the solution is ~30% of it, and the 22 minutes
# of Spatial.Host.Tests is in there whether or not the branch touched the host.
# What enforces what did not change, only where it runs: the formatter and the
# full suite are enforced by the pull request and the post-merge CI run, not by
# an agent's inner loop. ADR-0109 records the three lanes.
#
# `--quick` is accepted as a synonym for the default lane, from the draft of
# this script that had it as a flag.
set -euo pipefail
cd "$(dirname "$0")/.."

LANE=default
PLAN_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --full) LANE=full ;;
    --format) LANE=format ;;
    --quick) LANE=default ;;
    --plan) PLAN_ONLY=1 ;;
    -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

# Run a gate step, or just print it under --plan.
step() {
  if [[ "$PLAN_ONLY" == "1" ]]; then
    echo "$*"
  else
    "$@"
  fi
}

# --- the change set --------------------------------------------------------
#
# tools/verify_scope.py walks `git diff --name-only <base>...HEAD` plus the
# dirty working tree, maps the changed files onto the projects that own them,
# and takes the test projects that reach those projects over the
# `ProjectReference` graph. When it cannot read the change set — an
# unresolvable base, a solution-wide file such as Directory.Packages.props —
# it says so, and the lane falls back to everything rather than testing
# nothing. VERIFY_BASE moves the base for a measurement or a rebase.
BASE="${VERIFY_BASE:-origin/main}"
SCOPE_ARGS=(--base "$BASE")
EXHAUSTIVE=0
FORMAT_PROJECTS=()
TEST_PROJECTS=()
RUN_TOOLING=0
while IFS= read -r line; do
  case "$line" in
    format:*) FORMAT_PROJECTS+=("${line#format:}") ;;
    tests:*) TEST_PROJECTS+=("${line#tests:}") ;;
    tooling:*) RUN_TOOLING="${line#tooling:}" ;;
    exhaustive:*) EXHAUSTIVE="${line#exhaustive:}" ;;
  esac
done < <(python3 tools/verify_scope.py "${SCOPE_ARGS[@]}" --list plan --machine)

# The unscoped answer is the whole solution, so a lane that cannot scope runs
# the whole thing for that step rather than a guess at a narrow one.
scoped_or_all() {
  if [[ "$EXHAUSTIVE" == "1" ]]; then
    echo "SpatialEngine.slnx"
  else
    printf '%s\n' "${FORMAT_PROJECTS[@]:-}"
  fi
}

# --- the format lane -------------------------------------------------------
if [[ "$LANE" == "format" ]]; then
  echo "== format check (scoped to the changed projects) =="
  if [[ "$EXHAUSTIVE" == "1" ]]; then
    echo "the change set is unscoped against $BASE, so this is the whole solution"
  fi
  for project in $(scoped_or_all); do
    [[ -n "$project" ]] || continue
    # Per project, not per solution: the cost is the MSBuild workspace load,
    # and loading 55 projects is what makes the solution-wide run ~700 s.
    step dotnet format "$project" --verify-no-changes
  done
  exit 0
fi

# --- the full lane ---------------------------------------------------------
if [[ "$LANE" == "full" ]]; then
  echo "== format check =="
  # No --no-restore: a clean checkout has no project.assets.json yet, and the
  # format check loads every project through MSBuild before the build step runs.
  step dotnet format SpatialEngine.slnx --verify-no-changes

  echo "== build =="
  step dotnet build SpatialEngine.slnx

  echo "== tests =="
  step dotnet test SpatialEngine.slnx --no-build

  echo "== tooling tests =="
  # The repo also carries Python tooling (tools/), and this lane is the gate
  # before a pull request, so its unit tests run here too.
  step python3 -m unittest discover --start-directory tools --pattern 'test_*.py'
  exit 0
fi

# --- the default lane: the build gate --------------------------------------
echo "== build gate (base $BASE) =="

# The build restores implicitly; a separate `dotnet restore` in front of it is
# the 65 seconds the flat gate never spent on a second pass.
step dotnet build SpatialEngine.slnx

if [[ "$EXHAUSTIVE" == "1" ]]; then
  echo "== the change set is unscoped against $BASE, so every test project runs =="
  step dotnet test SpatialEngine.slnx --no-build
  step python3 -m unittest discover --start-directory tools --pattern 'test_*.py'
  exit 0
fi

echo "== tests that reach the change =="
for project in "${TEST_PROJECTS[@]}"; do
  step dotnet test "$project" --no-build
done

if [[ "$RUN_TOOLING" == "1" ]]; then
  echo "== tooling tests =="
  step python3 -m unittest discover --start-directory tools --pattern 'test_*.py'
else
  echo "== tooling tests skipped: tools/** did not change =="
fi
