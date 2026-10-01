# Releasing

The product version is single-sourced in `Directory.Build.props` (`<Version>`),
mirrored in `docs/CHANGELOG.md`, and never declared in individual projects.
The changelog is a release artefact rather than agent context, which is why it
lives under `docs/` rather than at the repository root (ADR-0148); a gate
(`tools/doc_surface.py`) fails on a second changelog and on a release heading
above `<Version>`.
Its release sections are **generated**, from the history, by
`tools/changelog.py` — a lane fails on a hand-merged `## [Unreleased]` section,
because that one was 1038 lines edited across 107 merges by no rule at all
(ADR-0173). Between releases, `git log v<previous>..HEAD` is what says what
shipped.

## Checklist

1. **Green gate from a clean checkout.** `./eng/verify.sh --full` — the full
   lane, which is what "green" means for a release; the bare `eng/verify.sh` is
   the scoped build gate (ADR-0118) — then
   `./eng/e2e-web.sh` and `./eng/workbench-e2e.sh`. CI runs all three plus the
   JavaScript suites on every push and pull request.
2. **Update the version.** Bump `<Version>` in `Directory.Build.props`.
3. **Generate the changelog.** There is no `## [Unreleased]` section to move:
   the release section is rendered from the history, so

   ```bash
   python3 tools/changelog.py --range v0.$(previous).0..HEAD \
       --date $(date +%F) --write
   ```

   writes `## [<Version>] - <date>` above the previous release in
   `docs/CHANGELOG.md`, one entry per bead, from the `Task:` trailer, the bead
   title, the `ADR-NNNN` ids the change cites and the narrative its work commits
   carry (ADR-0173). `--print` renders it without writing. Add or reword
   anything the release wants to say *inside the released section*: it is frozen
   history from then on, and `--verify-release <version>` checks it against the
   history the next time it runs.
4. **Refresh the SDK snapshot.** `eng/e2e-web.sh` regenerates the OpenAPI
   snapshot and the TypeScript wire types; it must leave `clients/typescript`
   clean.
5. **Commit and tag.** Commit the version, the changelog and the refreshed
   snapshot with `Release: X.Y.Z` in the body — the trailer that names the
   commit which *is* the release, so rendering `v0.3.0..v0.4.0` after the tag
   gives the section it gave before it — then
   `git tag -a vX.Y.Z -m "Spatial Engine X.Y.Z"` on that commit and push the
   tag.
6. **Build the image** (optional, for a server deployment):
   `docker build -t spatial-engine:X.Y.Z .`

## Container image

`Dockerfile` builds the workbench, publishes `Spatial.Host`, and serves both
from one origin on port 8080 as a non-root user. Configuration is environment
based:

| Setting | Environment variable | Default |
| --- | --- | --- |
| Workbench static root | `SPATIAL__WEBROOT` | `/app/webroot` (in the image) |
| PostGIS connection | `SPATIAL_POSTGIS_CONNECTION` | unset → store reports `store.unavailable` |
| Bind address | `ASPNETCORE_URLS` | `http://+:8080` |

```bash
docker run --rm -p 8080:8080 \
  -e SPATIAL_POSTGIS_CONNECTION="Host=…;Database=…;Username=…;Password=…" \
  spatial-engine:0.1.0
```

The demo store and the GeoServices FeatureServer are always available; the
PostGIS store is keyed `postgis` and only advertised once configured.
