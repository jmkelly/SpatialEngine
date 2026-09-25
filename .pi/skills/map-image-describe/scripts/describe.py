#!/usr/bin/env python3
"""Deterministic textual fingerprint of a MapServer export image.

Reads a PNG/JPEG map image (file path or http(s) URL, e.g. a
MapServer /export?f=image result) and prints a canonical text
description. Same input bytes always produce the same output text,
so two renders can be compared with diff, and two descriptions can
be compared with --compare for a similarity verdict.

Backend: ImageMagick (`magick`) for decode/normalize, everything else
is stdlib (hashlib, urllib, subprocess). No pip packages required.

Usage:
    describe.py <file|url> [--grid 16x12]
    describe.py --compare <A> <B> [--grid 16x12]
    describe.py --compare <A> <B> --text   # compare two description files

Exit codes:
    describe mode запускается всегда с 0 при успехе, 2 при ошибке.
    compare mode: 0 identical/near-identical, 1 similar, 2 different,
    3 on error.
"""

import hashlib
import os
import re
import subprocess
import sys
import tempfile
import urllib.request

GRID_DEFAULT = "16x12"
LEVELS = "0123456789"  # 0 = dark, 9 = light; digits survive trimming/diff


def fail(msg):
    print(f"describe: error: {msg}", file=sys.stderr)
    sys.exit(3 if "--compare" in sys.argv else 2)


def run_magick(args, stdin_data=None):
    try:
        p = subprocess.run(
            ["magick"] + args,
            input=stdin_data,
            capture_output=True,
            timeout=120,
        )
    except FileNotFoundError:
        fail("`magick` (ImageMagick 7) not found on PATH")
    except subprocess.TimeoutExpired:
        fail("magick timed out")
    if p.returncode != 0:
        fail(f"magick failed: {p.stderr.decode('utf-8', 'replace').strip()[:300]}")
    return p.stdout


def fetch_source(source):
    """Return (local_path, tmpdir_or_None). Downloads URLs via stdlib."""
    if re.match(r"^https?://", source):
        tmp = tempfile.mkdtemp(prefix="map-image-describe-")
        path = os.path.join(tmp, "image")
        try:
            req = urllib.request.Request(
                source, headers={"User-Agent": "map-image-describe/1.0"}
            )
            with urllib.request.urlopen(req, timeout=60) as r, open(path, "wb") as f:
                f.write(r.read())
        except Exception as e:  # noqa: BLE001 - report any fetch failure uniformly
            fail(f"could not fetch URL: {e}")
        return path, tmp
    if not os.path.isfile(source):
        fail(f"no such file: {source}")
    return source, None


def gray_bytes(path, w, h):
    """Normalized grayscale thumbnail as bytes (w*h values, 0=dark 255=light)."""
    out = run_magick(
        [path, "-resize", f"{w}x{h}!", "-colorspace", "Gray", "-depth", "8", "gray:-"]
    )
    want = w * h
    if len(out) < want:
        fail(f"unexpected gray pipe length ({len(out)} < {want})")
    return out[:want]


def bits_to_hex(bits):
    return f"{int(''.join('1' if b else '0' for b in bits), 2):016x}"


def ahash(px):
    mean = sum(px) / len(px)
    return bits_to_hex([v > mean for v in px])


def dhash(path):
    # 9x8 gray -> 8x8 horizontal differences -> 64 bits
    px = gray_bytes(path, 9, 8)
    bits = [
        px[y * 9 + x] > px[y * 9 + x + 1] for y in range(8) for x in range(8)
    ]
    return bits_to_hex(bits)


def hamming(hex_a, hex_b):
    return bin(int(hex_a, 16) ^ int(hex_b, 16)).count("1")


def describe(path):
    with open(path, "rb") as f:
        raw = f.read()
    sha = hashlib.sha256(raw).hexdigest()

    ident = run_magick([path, "-format", "%m %w %h", "info:"]).decode().strip().split()
    if len(ident) != 3:
        fail(f"could not identify image: {' '.join(ident)}")
    fmt, width, height = ident[0].upper(), int(ident[1]), int(ident[2])

    stats = (
        run_magick(
            [path, "-format", "%[fx:mean],%[fx:standard_deviation]", "info:"]
        )
        .decode()
        .strip()
        .split(",")
    )
    mean01, std01 = float(stats[0]), float(stats[1])

    mean_rgb = run_magick([path, "-resize", "1x1!", "-depth", "8", "rgb:-"])
    mr, mg, mb = mean_rgb[0], mean_rgb[1], mean_rgb[2]

    try:
        unique = int(
            run_magick([path, "-format", "%k", "info:"]).decode().strip().split()[0]
        )
    except (ValueError, IndexError):
        unique = -1  # delegate without histogram support; still deterministic

    gw, gh = (int(v) for v in GRID.split("x"))
    thumb = gray_bytes(path, gw, gh)
    a_hex = ahash(thumb)
    d_hex = dhash(path)
    rows = [
        "".join(LEVELS[v * 10 // 256] for v in thumb[y * gw : (y + 1) * gw])
        for y in range(gh)
    ]
    ink = sum(1 for v in thumb if v < 128) / len(thumb)

    lines = [
        f"format: {fmt}",
        f"width: {width}",
        f"height: {height}",
        f"bytes: {len(raw)}",
        f"sha256: {sha}",
        f"ahash: {a_hex}",
        f"dhash: {d_hex}",
        f"mean_rgb: {mr},{mg}, {mb}".replace(" ", ""),
        f"mean_hex: #{mr:02x}{mg:02x}{mb:02x}",
        f"mean_gray: {mean01:.4f}",
        f"std_gray: {std01:.4f}",
        f"unique_colors: {unique}",
        f"ink_ratio: {ink:.4f}",
        f"grid: {gw}x{gh} 0=dark 9=light",
    ]
    lines += [f"row_{y:02d}: {r}" for y, r in enumerate(rows)]
    return "\n".join(lines) + "\n"


def parse_desc(text):
    d = {}
    rows = []
    for line in text.splitlines():
        if line.startswith("row_"):
            rows.append(line.split(":", 1)[1].strip())
        elif ":" in line:
            k, v = line.split(":", 1)
            d[k.strip()] = v.strip()
    d["_rows"] = rows
    return d


def compare_text(a_text, b_text):
    a, b = parse_desc(a_text), parse_desc(b_text)
    out = []
    if a.get("sha256") and a.get("sha256") == b.get("sha256"):
        out.append("verdict: identical (sha256 match)")
        out.append("ahash_hamming: 0/64")
        out.append("dhash_hamming: 0/64")
        out.append("mean_color_dist: 0.00")
        return "\n".join(out) + "\n", 0

    ah = hamming(a["ahash"], b["ahash"])
    dh = hamming(a["dhash"], b["dhash"])
    am = [int(v) for v in a["mean_rgb"].split(",")]
    bm = [int(v) for v in b["mean_rgb"].split(",")]
    dist = sum((x - y) ** 2 for x, y in zip(am, bm)) ** 0.5
    size_eq = (
        a.get("width") == b.get("width") and a.get("height") == b.get("height")
    )
    fmt_eq = a.get("format") == b.get("format")

    if ah <= 2 and dh <= 2 and dist < 5.0:
        verdict, code = "near-identical (renderer noise only)", 0
    elif ah <= 8 and dh <= 8 and dist < 40.0:
        verdict, code = "similar (same scene, visible differences)", 1
    else:
        verdict, code = "different", 2

    out.append(f"verdict: {verdict}")
    out.append(f"ahash_hamming: {ah}/64")
    out.append(f"dhash_hamming: {dh}/64")
    out.append(f"mean_color_dist: {dist:.2f} (0-441 euclidean rgb)")
    out.append(f"size_equal: {str(size_eq).lower()}")
    out.append(f"format_equal: {str(fmt_eq).lower()}")
    out.append(f"a_mean_hex: {a.get('mean_hex')} vs b_mean_hex: {b.get('mean_hex')}")
    out.append(
        f"a_ink: {a.get('ink_ratio')} vs b_ink: {b.get('ink_ratio')}"
    )
    return "\n".join(out) + "\n", code


GRID = GRID_DEFAULT

args = sys.argv[1:]
if "--grid" in args:
    i = args.index("--grid")
    try:
        GRID = args.pop(i + 1)
        args.pop(i)
    except IndexError:
        fail("--grid needs a WxH value, e.g. --grid 16x12")
    if not re.fullmatch(r"\d+x\d+", GRID):
        fail("--grid needs a WxH value, e.g. --grid 16x12")

if "--compare" in args:
    args.remove("--compare")
    from_text = "--text" in args
    if from_text:
        args.remove("--text")
    if len(args) != 2:
        fail("usage: describe.py --compare <A> <B> [--grid WxH] | --compare <A.desc> <B.desc> --text")
    if from_text:
        with open(args[0], encoding="utf-8") as f:
            ta = f.read()
        with open(args[1], encoding="utf-8") as f:
            tb = f.read()
    else:
        pa, ta_tmp = fetch_source(args[0])
        pb, tb_tmp = fetch_source(args[1])
        try:
            ta, tb = describe(pa), describe(pb)
        finally:
            for t in (ta_tmp, tb_tmp):
                if t:
                    import shutil

                    shutil.rmtree(t, ignore_errors=True)
    report, code = compare_text(ta, tb)
    sys.stdout.write(report)
    sys.exit(code)

if len(args) != 1:
    fail("usage: describe.py <file|url> [--grid WxH]  |  describe.py --compare <A> <B>")
path, tmp = fetch_source(args[0])
try:
    sys.stdout.write(describe(path))
finally:
    if tmp:
        import shutil

        shutil.rmtree(tmp, ignore_errors=True)
