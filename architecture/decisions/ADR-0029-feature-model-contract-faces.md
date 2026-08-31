# ADR-0029: Feature model contract faces and the codec namespace

Status: Accepted

## Context

Phase 8 (plan §16, ADR-0028) made `Spatial.Core.Features` the data-provider
hub the core boundary always intended it to be (plan §6.1: "almost every
spatial plugin must exchange it"). The code-metrics gate's
`architectural-rigidity` diagnosis (Dependably.CodeMetrics `ForNamespace`:
zone of pain when an abstractness < 0.3, Ca ≥ 8, D ≥ 0.6 namespace is
concrete and widely depended on) subsequently flagged the namespace — while
`Spatial.Core.Geometry` had already been held to the same rule by the
fan-in discipline (Ca < 8, plan §16; the ADR-0028 budget). `Core.Features`
cannot be held to a referrer-count discipline: a data provider's many
feature-touching adapters each legitimately name several of its types, so
Ca is structurally high.

The repo's own convention resolves this class of finding: hub namespaces
carry the abstraction they are depended on through — `Spatial.PluginSdk.
Capabilities` (Ca 94, abstractness 0.35) and `Spatial.PluginSdk.Resources`
(Ca 31, 0.38) live on their interface faces, and `Spatial.Core.Geometry`
itself has `IGeometry`/`ICoordinateSequence`. The feature model had no
interfaces because nothing depended on it broadly before Phase 8.

## Decision

1. **The feature model gains its contract faces**: `IFeature`,
   `IFeatureSchema`, `IFeatureBatch` and `IFieldDefinition` in
   `Spatial.Core.Features`, implemented by `Feature`, `FeatureSchema`,
   `FeatureBatch` and `FieldDefinition` (concrete values stay the immutable
   implementations, ADR-0004). Contract-side code binds the interfaces where
   it only inspects/transports the model: `DatasetDescription.Schema`
   (`Spatial.PluginSdk.Providers`) and the PostGIS provider's
   schema-consuming adapters (`PostgisFilterSql`, `PostgisRowMapper`,
   `PostgisQueries`, the batch emitter/stream) are typed against
   `IFeatureSchema`. Construction stays concrete — the provider casts at
   the two construction sites (`Feature` ctor, `FeatureBatch` ctor) with an
   explicit guard.
2. **`FeatureBatchCodec` (and its private `Reader`/`Writer`/`AttributeSite`)
   moves to `Spatial.Core.Features.Codec`.** Encoding is a distinct
   structural concern from the value model; the split keeps the value
   namespace's abstractness above the diagnosis threshold without
   per-referrer bookkeeping. The codec's public class name, format version
   and bytes are unchanged — only the namespace changes (callers add one
   `using`).
3. The `architectural-rigidity` diagnosis continues to guide the fan-in
   discipline for `Spatial.Core.Geometry` (Ca < 8); the feature model's
   hub role is now carried by its contract faces instead.

## Consequences

- The metrics gate is green with honest structure: `Core.Features`
  abstractness 0.33 (the repo's hub-namespace norm), `Core.Geometry` Ca 7,
  the codec namespace a leaf (Ca 1, D 0.09).
- Public surface changed deliberately: four new interfaces (additive), the
  `IsDecodableFrom`/`TryIsDecodableFrom`/`FieldDefinition.IsDecodableFrom`
  parameters widened from the concrete type to the interface
  (source-compatible for callers), and the codec's namespace. Tests, SDK
  contracts, `Spatial.Core/AGENTS.md`, `architecture/feature-model.md` and
  this ADR record the change together (plan §20/§22).
- Phase 9's host and SDKs can bind `IFeature`/`IFeatureSchema`/`IFeatureBatch`
  — the natural contract face for the HTTP/streaming API layers.