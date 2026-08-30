using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The <c>spatial.feature.query@1</c> capability contract (plan §11
/// "bounding-box and parameterised attribute filtering", §16 Phase 8,
/// ADR-0028): scans a dataset through optional spatial and attribute filters.
/// The spatial filter is an axis-aligned bounding box (<c>minx/miny/maxx/
/// maxy</c>, all-or-none, in the dataset's coordinate space — x-first, the
/// engine's convention); the attribute filter is a small parameterised
/// expression language over the dataset's fields: comparisons
/// (<c>= != &lt;&gt; &lt; &lt;= &gt; &gt;= LIKE</c>), <c>IS NULL</c> /
/// <c>IS NOT NULL</c>, boolean values, <c>AND</c>/<c>OR</c> and parentheses.
/// Values are always bound parameters — a filter is never concatenated into
/// SQL, and column names must match a discovered field. The result is a
/// bounded stream of canonical feature batches, exactly like a scan. The
/// embedded examples are the DB-free argument shapes; the store-backed
/// filtering matrix ships with the integration suite.
/// </summary>
public static class FeatureQueryContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.feature.query@1");

    /// <summary>The input interchange shape: dataset, optional bbox and optional attribute filter.</summary>
    public const string InputSchema = "feature.query";

    /// <summary>The output interchange shape: canonical feature batches on a bounded stream.</summary>
    public const string OutputSchema = "feature.batch";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Streams the features of a dataset matching an optional bounding box (minx/miny/maxx/maxy) and an optional parameterised attribute filter, as canonical feature batches.",
        new SchemaDescriptor(InputSchema, "'dataset'; optional bbox 'minx','miny','maxx','maxy' (doubles, all-or-none); optional 'filter' (expression over the dataset's fields)."),
        new SchemaDescriptor(OutputSchema, "A bounded stream of canonical feature-batch byte arrays for the matching features."),
        [new ErrorVariant("invalid.arguments", "The dataset id, bbox or filter expression are missing, malformed or invalid; unknown filter columns are invalid arguments naming the available fields.")],
        [Permission.Parse("spatial.feature.read")],
        CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
        [
            new ConformanceExample(
                "bounding-box-only",
                "A bbox-only query returns the features whose geometry intersects the box.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places", ProviderArguments.MinX, 13.0, ProviderArguments.MinY, 52.0, ProviderArguments.MaxX, 14.0, ProviderArguments.MaxY, 53.0)),
            new ConformanceExample(
                "incomplete-bbox",
                "A partial bbox (some but not all bounds) is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places", ProviderArguments.MinX, 13.0)),
            new ConformanceExample(
                "non-numeric-bbox",
                "A non-numeric bbox bound is an invalid argument.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places", ProviderArguments.MinX, "13.0")),
            new ConformanceExample(
                "malformed-filter",
                "A filter that cannot be parsed is an invalid argument naming the position.",
                ContractArguments.Build(ProviderArguments.Dataset, "public.places", ProviderArguments.Filter, "population >")),
        ]);
}
