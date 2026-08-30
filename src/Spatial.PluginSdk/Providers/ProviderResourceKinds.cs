using Spatial.PluginSdk.Resources;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The resource kinds data providers mint through the invocation facilities
/// (plan §8 "opaque handles for datasets, transactions and intermediate
/// results", ADR-0022): a <c>transaction</c> handle returned by
/// <c>spatial.transaction.begin@1</c> and referenced by commit/rollback and
/// enlisted writes. Stream kinds are diagnostics-only and follow the same
/// dotted rule.
/// </summary>
public static class ProviderResourceKinds
{
    /// <summary>A live database transaction (commit/rollback/enlist by handle).</summary>
    public static ResourceKind Transaction { get; } = new("transaction");

    /// <summary>A bounded stream of canonical feature batches (scan/query).</summary>
    public static ResourceKind FeatureStream { get; } = new("feature.stream");

    /// <summary>A bounded stream of catalogue metadata JSON items (catalogue.list).</summary>
    public static ResourceKind CatalogueStream { get; } = new("catalogue.metadata");

    /// <summary>A bounded stream of dataset description JSON items (dataset.describe).</summary>
    public static ResourceKind DatasetStream { get; } = new("dataset.metadata");
}
