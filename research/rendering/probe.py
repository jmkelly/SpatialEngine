#!/usr/bin/env python3
"""Probe the .NET 2D-rendering candidate field.

This is the data-collection half of the rendering autoresearch loop: it asks
NuGet and GitHub for the facts that decide a rendering library (maintenance,
reach, licence, native/GPU posture) and prints a markdown-ready report. It
does not decide anything; `README.md` is the report and the recommendation.

    python3 research/rendering/probe.py

Network is required. GitHub's unauthenticated API is rate-limited; set
GITHUB_TOKEN to lift the limit.
"""

from __future__ import annotations

import json
import os
import sys
import urllib.parse
import urllib.request

NUGET_PACKAGES = [
    "SkiaSharp",
    "SkiaSharp.NativeAssets.Linux.NoDependencies",
    "SkiaSharp.HarfBuzz",
    "NetVips",
    "NetVips.Native.linux-x64",
    "Svg.Skia",
    "SixLabors.ImageSharp.Drawing",
    "Magick.NET-Q8-AnyCPU",
    "Mapsui.Rendering.Skia",
    "ExCSS",
]

GITHUB_REPOS = [
    "mono/SkiaSharp",
    "kleisauke/net-vips",
    "libvips/libvips",
    "wieslawsoltes/Svg.Skia",
    "SixLabors/ImageSharp",
    "mapsui/mapsui",
    "mapnik/mapnik",
    "linebender/vello",
    "maplibre/maplibre-native",
]


def fetch_json(url: str) -> dict:
    headers = {"User-Agent": "spatial-engine-rendering-research"}
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def nuget_table() -> None:
    print("## NuGet\n")
    print("| Package | Latest | Total downloads | Description |")
    print("| --- | --- | --- | --- |")
    for package in NUGET_PACKAGES:
        url = "https://azuresearch-usnc.nuget.org/query?" + urllib.parse.urlencode(
            {"q": f'packageid:{package}', "prerelease": "false", "take": 1}
        )
        try:
            data = fetch_json(url).get("data", [])
            if not data:
                print(f"| {package} | (not found) | | |")
                continue
            item = data[0]
            downloads = f"{item.get('totalDownloads', 0):,}"
            description = (item.get("description") or "").replace("|", "/")[:64]
            print(f"| {package} | {item['version']} | {downloads} | {description} |")
        except Exception as error:  # keep the probe going
            print(f"| {package} | error: {error} | | |")


def github_table() -> None:
    print("\n## GitHub\n")
    print("| Repo | Stars | Last push | Licence | Archived |")
    print("| --- | --- | --- | --- | --- |")
    for repo in GITHUB_REPOS:
        try:
            data = fetch_json(f"https://api.github.com/repos/{repo}")
            licence = (data.get("license") or {}).get("spdx_id")
            print(
                f"| {repo} | {data.get('stargazers_count', '?'):,} | "
                f"{data.get('pushed_at', '?')[:10]} | {licence} | {data.get('archived')} |"
            )
        except Exception as error:
            print(f"| {repo} | error: {error} | | | |")


def main() -> int:
    print("# Rendering candidate probe\n")
    nuget_table()
    github_table()
    print("\nLocal toolchain:")
    print(f"- dotnet: {os.popen('dotnet --version').read().strip()}")
    vips = os.popen("pkg-config --modversion vips 2>/dev/null").read().strip()
    print(f"- libvips: {vips or '(not installed)'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
