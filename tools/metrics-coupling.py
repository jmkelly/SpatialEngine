#!/usr/bin/env python3
"""Approximate the in-repo coupling of a type: which in-repo types its source mentions.

Usage: python3 tools/metrics-coupling.py <TypeName> [<TypeName> ...]
Reads metrics-report.json for the in-repo type roster, then greps each type's
declaration span (from the type's StartLine to the start of the next type in the
same file) for those type names. Approximate, but good enough to plan
extractions; the codemetrics audit remains the source of truth.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
report = json.loads((ROOT / "metrics-report.json").read_text())
types = report["extra"]["metrics"]["Types"]
by_name: dict[str, list] = {}
for t in types:
    by_name.setdefault(t["Name"], []).append(t)

for name in sys.argv[1:]:
    for t in by_name[name]:
        path = ROOT / t["File"]
        lines = path.read_text().splitlines()
        # declaration span: brace-matched from the type's declaration line
        depth = 0
        started = False
        body_lines: list[str] = []
        for line in lines[t["StartLine"] - 1 :]:
            body_lines.append(line)
            depth += line.count("{") - line.count("}")
            if "{" in line:
                started = True
            if started and depth <= 0:
                break
        body = "\n".join(body_lines)
        found = sorted({x["Name"] for x in types if x["Name"] != name and re.search(rf"\b{re.escape(x['Name'])}\b", body)})
        print(f"{t['Name']} ({t['File']}:{t['StartLine']}) coupling={t['InRepoCoupling']} wmc={t['WeightedMethods']} lcom={t['Lcom4']} m={t['MethodCount']} f={t['FieldCount']} ifaces={t['InterfaceCount']}")
        print(f"  mentions {len(found)}: {', '.join(found)}")
