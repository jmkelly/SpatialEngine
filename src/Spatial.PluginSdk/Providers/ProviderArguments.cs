namespace Spatial.PluginSdk.Providers;

/// <summary>
/// Stable argument names of the data-provider capability contracts (plan
/// §11, ADR-0028): catalogue, dataset and feature capability inputs share
/// one vocabulary so providers and clients agree on every parameter.
/// Multi-value inputs that the inline wire protocol cannot carry (feature
/// batches, bounding boxes) travel as canonical binary arguments
/// (<c>$bytes</c>) or as separate scalar arguments; feature data is never an
/// inline spatial value (ADR-0020 — batches cross as streams or canonical
/// binary arguments).
/// </summary>
public static class ProviderArguments
{
    /// <summary>A dataset identifier (<c>schema.table</c> or <c>table</c>).</summary>
    public const string Dataset = "dataset";

    /// <summary>An optional dataset-name pattern with SQL LIKE wildcards.</summary>
    public const string Pattern = "pattern";

    /// <summary>A canonical feature batch (FeatureBatchCodec v1 bytes).</summary>
    public const string Batch = "batch";

    /// <summary>An SRID for result-table creation (dataset.create).</summary>
    public const string Srid = "srid";

    /// <summary>The bounding-box minimum X for a spatial filter (feature.query).</summary>
    public const string MinX = "minx";

    /// <summary>The bounding-box minimum Y for a spatial filter (feature.query).</summary>
    public const string MinY = "miny";

    /// <summary>The bounding-box maximum X for a spatial filter (feature.query).</summary>
    public const string MaxX = "maxx";

    /// <summary>The bounding-box maximum Y for a spatial filter (feature.query).</summary>
    public const string MaxY = "maxy";

    /// <summary>An attribute filter expression over the dataset's fields (feature.query).</summary>
    public const string Filter = "filter";

    /// <summary>A transaction handle returned by spatial.transaction.begin@1.</summary>
    public const string Transaction = "transaction";
}
