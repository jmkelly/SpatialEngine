# Spatial.Adapter.GeoServices

The Esri GeoServices REST boundary adapter: the Feature/Map/Tiles/Image/
Wms/Wfs surface, served from the public contracts. Two records bind it:
**ADR-0035** (GeoServices REST is an adapter-owned boundary; parity is against
`architecture/references/geoservices-compatibility.md`, not against memory)
and **ADR-0112** (each read surface pushes what the store can answer and keeps
what it must). Route by task: `architecture/principles.md`.

## Never

- No "fixing" a divergence from memory of the spec. The compatibility
  reference in this repository is the spec here; where it disagrees with the
  real service, the reference is what gets amended, and the record says so.
- No parameter accepted and ignored: a parameter is honoured or rejected by
  name, and the rejection names the supported spelling (ADR-0035).
- No third-party or Esri object on a contract — the adapter maps protocol
  verbs onto `Spatial.Contracts` faces and returns core-typed values.
- No pushdown that answers a different question than the scan would
  (ADR-0112); identify pushes the query envelope's box only.
- No invented capability: a flag advertised on a layer is what the client may
  rely on, and a capability the store cannot honour is refused rather than
  emulated.

## Commands

- `dotnet test tests/unit/Spatial.Adapter.GeoServices.Tests` — the parity
  battery against the reference.
- `dotnet test tests/conformance/Spatial.PredicateConformance/Spatial.PredicateConformance.csproj` — the DE-9IM conformance the host and every client share.
- `eng/e2e-web.sh` — a real host serving a real client.
