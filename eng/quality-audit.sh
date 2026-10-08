#!/usr/bin/env bash
# The quality loop, run as a step: the five code audits the skill owns, plus
# the documentation audit this repository owns, aggregated into one answer.
#
#   eng/quality-audit.sh              run the doc audit, then aggregate what is
#                                     already on disk (reporting only)
#   eng/quality-audit.sh --docs       the doc audit alone
#   eng/quality-audit.sh --report     one line — the count and the two
#                                     artefacts — and nothing that can fail.
#                                     This is what a gate lane runs: a lane
#                                     prints its steps, so twenty lines of
#                                     quality-loop table under the doc gate is
#                                     noise the reader has to scroll past
#   eng/quality-audit.sh --run        run the code audits too, if the quality-loop
#                                     skill is installed (slow: ~11 min with
#                                     Stryker), then aggregate
#   eng/quality-audit.sh --plan       print what it would run, and run nothing
#
# The five code audits live in the `quality-loop` skill rather than here, because
# they are tool-specific and installable rather than repository policy
# (crap4dotnet, coverlet, Dependably.CodeMetrics, `dotnet build` warnings,
# dotnet-stryker). What is repository policy is the floor
# (`coverage-policy.json`), the rules (`.dependably`) and this aggregator: one
# command that answers "what is failing, across code and documentation", over
# the artefacts every one of those audits already writes.
#
# The documentation audit is `tools/doc-freshness.py`, and it is the reason this
# script exists rather than a bare `scripts/quality-loop.py` invocation: the
# skill audits code five ways and documentation zero, which is how a SKILL.md
# pointing at files that do not exist survived. The documentation audit
# reports dead links, unresolvable skill pointers, bloated instruction files,
# conflicting gate claims and walls the prose denies.
#
# REPORTING ONLY, everywhere in here. `doc-freshness.py` has no `--check` mode
# on purpose: the queue has to drain before any of the six new checks is
# promoted, and a gate over a queue nobody can clear in the time they have is a
# red lane that teaches nothing. The three shared checks are the exception, and
# they are gates already — through `eng/verify.sh`, not through this script.
#
# And a green run here is not a correctness improvement, and must not be
# reported as one: arXiv 2607.27250 ran 288 agent runs across two frontier
# agents and found context strategy does not move correctness (<=10-15pp,
# equivalence-bounded). What the doc audit buys is fewer confidently-wrong
# answers and less token (arXiv 2601.20404: -28.6% median runtime, -16.6%
# output tokens).
set -euo pipefail
cd "$(dirname "$0")/.."

RUN_CODE_AUDITS=0
DOCS_ONLY=0
REPORT_ONLY=0
PLAN_ONLY=0
for arg in "$@"; do
  case "$arg" in
    --docs) DOCS_ONLY=1 ;;
    --report) REPORT_ONLY=1 ;;
    --run) RUN_CODE_AUDITS=1 ;;
    --plan) PLAN_ONLY=1 ;;
    -h|--help) sed -n '2,45p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

# The quality-loop skill, wherever it is installed. It is not a dependency: the
# code audits are the skill's, and a checkout without the skill still gets the
# doc audit and the aggregation.
QUALITY_SKILL="${QUALITY_SKILL:-}"
if [[ -z "$QUALITY_SKILL" ]]; then
  for candidate in "$HOME/.pi/agent/skills/quality-loop" "$HOME/.claude/skills/quality-loop"; do
    if [[ -f "$candidate/scripts/quality-loop.py" ]]; then
      QUALITY_SKILL="$candidate"
      break
    fi
  done
fi

step() {
  if [[ "$PLAN_ONLY" == "1" ]]; then
    echo "\$ $*"
  else
    "$@"
  fi
}

# --- the documentation audit ------------------------------------------------
# Every mode runs it. It is the audit this repository owns; the code audits
# belong to the skill and are opt-in (`--run`).
echo "== documentation freshness (reporting only) =="
if [[ "$REPORT_ONLY" == "1" ]]; then
  step python3 tools/doc-freshness.py --report --quiet
else
  step python3 tools/doc-freshness.py
fi

# --- the code audits, when asked for ----------------------------------------
if [[ "$RUN_CODE_AUDITS" == "1" ]]; then
  if [[ -z "$QUALITY_SKILL" ]]; then
    echo "quality-loop skill not found; set QUALITY_SKILL to its directory" >&2
    exit 2
  fi
  echo "== code quality loop (${QUALITY_SKILL}) =="
  step python3 "$QUALITY_SKILL/scripts/quality-loop.py" --dry-run
fi

# --- one answer over every report on disk -----------------------------------
# Every audit in the loop writes the same shape — a `*-report.json` beside a
# `*-queue.md` at the repository root, both gitignored — so the aggregate is a
# read of those files rather than a second opinion from this script. A report
# that is not on disk is a suite that was not run, and it says so rather than
# counting as green: an absent report is not a passing audit.
summarise() {
  local name="$1" report="$2" offenders='—'
  if [[ -f "$report" ]]; then
    offenders=$(python3 - "$report" <<'PY'
import json, sys
try:
    report = json.load(open(sys.argv[1], encoding="utf-8"))
except (OSError, ValueError):
    print("unreadable"); raise SystemExit(0)
stats = report.get("stats", {})
if isinstance(stats, dict):
    for key in ("offenders", "findings", "failures", "mutants", "queued"):
        if key in stats:
            print(stats[key]); raise SystemExit(0)
methods = report.get("methods") or report.get("findings") or []
print(len(methods))
PY
)
  fi
  printf '| %-22s | %-24s | %s |\n' "$name" "$report" "$offenders"
}

if [[ "$PLAN_ONLY" != "1" && "$DOCS_ONLY" != "1" && "$REPORT_ONLY" != "1" ]]; then
  echo
  echo "== the loop, one line per audit (offenders; a dash is a suite not run) =="
  echo "| audit | report | offenders |"
  echo "| --- | --- | --- |"
  summarise quality crap-report.json
  summarise coverage coverage-report.json
  summarise metrics metrics-report.json
  summarise warnings warnings-report.json
  summarise mutation stryker-report.json
  summarise documentation doc-report.json
  echo
  echo "Queues, in the order to drain them: crap-queue.md, coverage-queue.md,"
  echo "metrics-queue.md, warnings-queue.md, stryker-queue.md, doc-queue.md."
fi
