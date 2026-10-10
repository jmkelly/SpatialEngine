#!/usr/bin/env bash
# Package the QGIS plugin (clients/qgis) as a QGIS "Install from ZIP" archive.
#
# Usage:
#   eng/package-qgis-plugin.sh [--output <zip>] [--watch]
#
# One-shot (default) builds artifacts/qgis/spatial-engine-plugin.zip.
# --watch rebuilds whenever a file under clients/qgis changes
# (inotifywait when available, mtime polling otherwise).
set -euo pipefail
cd "$(dirname "$0")/.."

SRC="clients/qgis"
OUT="artifacts/qgis/spatial-engine-plugin.zip"
WATCH=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output) OUT="$2"; shift 2 ;;
    --watch) WATCH=1; shift ;;
    -h|--help)
      sed -n '2,10p' "$0"
      exit 0 ;;
    *) echo "unknown arg: $1" >&2; exit 1 ;;
  esac
done

package() {
  python3 - "$SRC" "$OUT" <<'PY'
import sys, zipfile
from pathlib import Path

src, out = Path(sys.argv[1]), Path(sys.argv[2])
plugin_dir = "spatial_engine"  # top-level folder QGIS unzips
excluded_dirs = {"__pycache__", "tests", ".pytest_cache"}

out.parent.mkdir(parents=True, exist_ok=True)
files = sorted(
    p for p in src.rglob("*")
    if p.is_file()
    and not any(part in excluded_dirs for part in p.relative_to(src).parts)
    and p.suffix not in {".pyc", ".pyo"}
)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as zf:
    for p in files:
        zf.write(p, plugin_dir + "/" + p.relative_to(src).as_posix())
print(f"packaged {len(files)} files -> {out}")
PY
}

package

if [[ "$WATCH" -eq 0 ]]; then
  exit 0
fi

echo "watching $SRC for changes (polling every 2s; Ctrl+C to stop)..."
last=""
while true; do
  current=$(find "$SRC" -type f -not -path '*/__pycache__/*' -not -path '*/tests/*' -not -path '*__pycache__*' -exec sha256sum {} + | sort)
  if [[ "$current" != "$last" ]]; then
    if [[ -n "$last" ]]; then
      package
    fi
    last="$current"
  fi
  sleep 2
done
