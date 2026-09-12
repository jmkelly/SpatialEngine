# Releasing

The product version is single-sourced in `Directory.Build.props` (`<Version>`),
mirrored in `CHANGELOG.md`, and never declared in individual projects.

## Checklist

1. **Green gate from a clean checkout.** `./eng/verify.sh`, then
   `./eng/e2e-web.sh` and `./eng/workbench-e2e.sh`. CI runs all three plus the
   JavaScript suites on every push and pull request.
2. **Update the version.** Bump `<Version>` in `Directory.Build.props`.
3. **Update the changelog.** Move `Unreleased` entries under a new
   `## [x.y.z] - YYYY-MM-DD` heading in `CHANGELOG.md`.
4. **Refresh the SDK snapshot.** `eng/e2e-web.sh` regenerates the OpenAPI
   snapshot and the TypeScript wire types; it must leave `clients/typescript`
   clean.
5. **Commit and tag.** `git tag -a vX.Y.Z -m "Spatial Engine X.Y.Z"` on the
   commit that carries the version and changelog, then push the tag.
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
