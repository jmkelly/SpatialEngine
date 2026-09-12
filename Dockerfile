# syntax=docker/dockerfile:1

# Spatial Engine ships as the independently executable .NET host with the
# browser workbench served from `Spatial:WebRoot` (ADR-0039). This image
# builds the workbench, publishes the host, and runs both with one origin.

# --- Stage 1: build the browser workbench --------------------------------
FROM node:26-bookworm-slim AS workbench
WORKDIR /src
# The workbench resolves `@spatial/client` through a `file:` dependency, so
# the SDK must sit at the same relative path it has in the repository. Copy
# manifests first so `npm ci` is cached until a lockfile changes.
COPY clients/typescript/package.json clients/typescript/package-lock.json clients/typescript/
COPY apps/workbench-web/package.json apps/workbench-web/package-lock.json apps/workbench-web/
RUN npm ci --prefix apps/workbench-web
COPY clients/typescript/ clients/typescript/
COPY apps/workbench-web/ apps/workbench-web/
RUN npm run build --prefix apps/workbench-web

# --- Stage 2: publish the host -------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Spatial.Host/Spatial.Host.csproj \
      --configuration Release \
      --output /app/publish \
      /p:UseAppHost=false

# --- Stage 3: runtime ----------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Same origin for API and workbench; a PostGIS connection string may be
# supplied as SPATIAL_POSTGIS_CONNECTION (never in request bodies).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    SPATIAL__WEBROOT=/app/webroot \
    DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish ./
COPY --from=workbench /src/apps/workbench-web/dist ./webroot

EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Spatial.Host.dll"]
