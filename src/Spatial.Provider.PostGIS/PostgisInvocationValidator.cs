using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Streams;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The pure, store-free argument-validation surface of the PostGIS provider
/// (ADR-0028): every guard the nine capability handlers run before touching
/// the database. Handlers chain the guards in contract order with
/// <see cref="FirstError"/> (all readers are deterministic over the
/// invocation alone, so eager evaluation is observationally identical to
/// short-circuiting) and only then delegate the database work to
/// <see cref="PostgisStoreExecutor"/>. Because no member here touches the
/// store or configuration state, the whole surface is unit-testable without
/// a container; the containerised integration suite covers everything below
/// it.
/// </summary>
internal static class PostgisInvocationValidator
{
    /// <summary>The default SRID for result tables created without an explicit one.</summary>
    internal const int DefaultCreateSrid = 4326;

    /// <summary>
    /// Returns the first non-null error of the guard list, or null when every
    /// guard passed. All guards are pure reads, so evaluating them eagerly
    /// changes nothing observable while keeping the handlers one branch deep.
    /// </summary>
    internal static CapabilityError? FirstError(params CapabilityError?[] errors)
    {
        foreach (var error in errors)
        {
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>A pre-cancelled invocation fails with a cancellable error before touching the store.</summary>
    internal static CapabilityError? CheckCancelled(CapabilityInvocation invocation) =>
        invocation.CancellationToken.IsCancellationRequested
            ? CapabilityError.Cancelled(invocation.Capability)
            : null;

    /// <summary>An invocation without runtime facilities cannot mint streams or resources.</summary>
    internal static CapabilityError? RequireFacilities(CapabilityInvocation invocation, out ICapabilityFacilities facilities)
    {
        if (invocation.Facilities is { } present)
        {
            facilities = present;
            return null;
        }

        facilities = null!;
        return NeedsFacilities(invocation);
    }

    internal static CapabilityError? ReadDataset(CapabilityInvocation invocation, out PostgisDatasetName dataset)
    {
        dataset = default;
        if (!invocation.TryGetArgument<string>(ProviderArguments.Dataset, out var text))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "a 'dataset' identifier (schema.table or table) is required.");
        }

        if (!PostgisDatasetName.TryParse(text, out dataset, out var reason))
        {
            return PostgisDiagnostics.InvalidArgument(invocation.Capability, reason);
        }

        return null;
    }

    internal static CapabilityError? ReadBatch(CapabilityInvocation invocation, out FeatureBatch batch)
    {
        batch = null!;
        if (!invocation.TryGetArgument<byte[]>(ProviderArguments.Batch, out var bytes))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "'batch' must carry a canonical feature batch (FeatureBatchCodec v1 bytes).");
        }

        if (FeatureBatchStream.TryDecode(bytes, out batch, out var reason))
        {
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, $"the 'batch' is not a valid canonical feature batch: {reason}");
    }

    /// <summary>Reads a canonical batch and validates its defining schema in one guard step.</summary>
    internal static CapabilityError? ReadBatchAndSchema(CapabilityInvocation invocation, out FeatureBatch batch)
    {
        if (ReadBatch(invocation, out batch) is { } error)
        {
            return error;
        }

        return ValidateBatchSchema(batch.Schema, invocation);
    }

    internal static CapabilityError? ReadSrid(CapabilityInvocation invocation, out int srid)
    {
        srid = DefaultCreateSrid;
        if (!invocation.Arguments.ContainsKey(ProviderArguments.Srid))
        {
            return null;
        }

        if (invocation.TryGetArgument<int>(ProviderArguments.Srid, out var value) && value >= 0)
        {
            srid = value;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, "'srid' must be a non-negative int32 when provided.");
    }

    internal static CapabilityError? ReadBoundingBox(CapabilityInvocation invocation, out BoundingBox? boundingBox)
    {
        boundingBox = null;
        var names = new[] { ProviderArguments.MinX, ProviderArguments.MinY, ProviderArguments.MaxX, ProviderArguments.MaxY };
        var present = names.Where(name => invocation.Arguments.ContainsKey(name)).ToArray();
        if (present.Length == 0)
        {
            return null;
        }

        if (present.Length != 4)
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the bounding box needs all four bounds: minx, miny, maxx, maxy.");
        }

        if (!TryReadBounds(invocation, out var minx, out var miny, out var maxx, out var maxy))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the bounding-box bounds minx/miny/maxx/maxy must be numbers.");
        }

        if (!IsValidBounds(minx, miny, maxx, maxy))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the bounding box is invalid: bounds must be finite with minx <= maxx and miny <= maxy.");
        }

        boundingBox = new BoundingBox(minx, miny, maxx, maxy);
        return null;
    }

    /// <summary>Reads the four bounds, accepting the wire's integral JSON number forms too.</summary>
    private static bool TryReadBounds(
        CapabilityInvocation invocation,
        out double minx,
        out double miny,
        out double maxx,
        out double maxy)
    {
        minx = 0;
        miny = 0;
        maxx = 0;
        maxy = 0;
        return TryReadNumber(invocation, ProviderArguments.MinX, out minx)
            && TryReadNumber(invocation, ProviderArguments.MinY, out miny)
            && TryReadNumber(invocation, ProviderArguments.MaxX, out maxx)
            && TryReadNumber(invocation, ProviderArguments.MaxY, out maxy);
    }

    /// <summary>Finite bounds in the min &lt;= max order the query contract requires.</summary>
    internal static bool IsValidBounds(double minx, double miny, double maxx, double maxy) =>
        double.IsFinite(minx) && double.IsFinite(miny) && double.IsFinite(maxx) && double.IsFinite(maxy)
        && minx <= maxx && miny <= maxy;

    /// <summary>Reads one bbox bound as a double, accepting the wire's integral JSON number forms too (int/long).</summary>
    private static bool TryReadNumber(CapabilityInvocation invocation, string name, out double value)
    {
        if (invocation.TryGetArgument<double>(name, out value))
        {
            return true;
        }

        if (invocation.TryGetArgument<int>(name, out var integer))
        {
            value = integer;
            return true;
        }

        if (invocation.TryGetArgument<long>(name, out var longInteger))
        {
            value = longInteger;
            return true;
        }

        value = 0;
        return false;
    }

    internal static CapabilityError? ReadFilter(CapabilityInvocation invocation, out string? filterText)
    {
        filterText = null;
        if (!invocation.Arguments.ContainsKey(ProviderArguments.Filter))
        {
            return null;
        }

        if (invocation.TryGetArgument<string>(ProviderArguments.Filter, out var text))
        {
            filterText = text;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, "'filter' must be a string expression.");
    }

    internal static CapabilityError? ParseFilter(CapabilityInvocation invocation, string? filterText, out FilterExpression? filter)
    {
        filter = null;
        if (filterText is null)
        {
            return null;
        }

        if (PostgisFilterParser.TryParse(filterText, out var expression, out var error))
        {
            filter = expression;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, $"the filter is not valid: {error}");
    }

    /// <summary>Reads and parses an optional filter expression in one guard step.</summary>
    internal static CapabilityError? TryReadFilter(CapabilityInvocation invocation, out FilterExpression? filter)
    {
        if (ReadFilter(invocation, out var text) is { } error)
        {
            filter = null;
            return error;
        }

        return ParseFilter(invocation, text, out filter);
    }

    internal static CapabilityError? ReadPattern(CapabilityInvocation invocation, out string? pattern)
    {
        pattern = null;
        if (!invocation.Arguments.ContainsKey(ProviderArguments.Pattern))
        {
            return null;
        }

        if (invocation.TryGetArgument<string>(ProviderArguments.Pattern, out var text))
        {
            pattern = text;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(invocation.Capability, "'pattern' must be a string.");
    }

    internal static CapabilityError? ReadOptionalTransaction(CapabilityInvocation invocation, out ResourceId? transactionId)
    {
        transactionId = null;
        if (!invocation.Arguments.ContainsKey(ProviderArguments.Transaction))
        {
            return null;
        }

        if (invocation.TryGetArgument<ResourceHandle>(ProviderArguments.Transaction, out var handle))
        {
            transactionId = handle.Id;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(
            invocation.Capability, "'transaction' must be a transaction handle from spatial.transaction.begin@1.");
    }

    internal static CapabilityError? ReadTransactionHandle(CapabilityInvocation invocation, out ResourceId handle)
    {
        handle = default;
        if (invocation.TryGetArgument<ResourceHandle>(ProviderArguments.Transaction, out var transaction))
        {
            handle = transaction.Id;
            return null;
        }

        return PostgisDiagnostics.InvalidArgument(
            invocation.Capability, "a 'transaction' handle from spatial.transaction.begin@1 is required.");
    }

    internal static CapabilityError? ValidateBatchSchema(FeatureSchema schema, CapabilityInvocation invocation)
    {
        foreach (var field in schema.Fields)
        {
            if (PostgisDatasetName.IsValidIdentifier(field.Name))
            {
                continue;
            }

            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability,
                $"the field name '{field.Name}' is not a valid column identifier; only lowercase [a-z0-9_] names are allowed.");
        }

        if (schema.Fields.All(field => field.Kind != AttributeKind.Geometry))
        {
            return PostgisDiagnostics.InvalidArgument(
                invocation.Capability, "the defining batch has no geometry field; a spatial result table needs one.");
        }

        return null;
    }

    internal static CapabilityError NotDecodable(CapabilityInvocation invocation, PostgisDatasetName dataset, DatasetDescription description)
    {
        var fields = string.Join(", ", description.Schema.Fields.Select(field => $"'{field.Name}'"));
        return PostgisDiagnostics.InvalidArgument(
            invocation.Capability,
            $"the batch schema is not decodable from '{dataset}' (fields: {fields}); write a batch carrying a prefix of the dataset's fields.");
    }

    internal static CapabilityError NeedsFacilities(CapabilityInvocation invocation) =>
        PostgisDiagnostics.InvalidArgument(invocation.Capability, "the invocation has no runtime facilities; only the runtime can mint streams and resources.");

    /// <summary>Builds the SQL predicate for a query's validated bbox and filter (pure, store-free).</summary>
    internal static PredicateBuild BuildPredicate(
        CapabilityInvocation invocation,
        DatasetDescription description,
        BoundingBox? boundingBox,
        FilterExpression? filter)
    {
        var values = new List<object?>();
        var parts = new List<string>(2);
        if (boundingBox is { } box)
        {
            parts.Add(PostgisFilterSql.BoundingBox(
                box, description.GeometryColumn, description.Srid, values));
        }

        if (filter is not null)
        {
            // Continue the positional-parameter numbering after the bounding
            // box's four values; a second builder restarting at @p0 would bind
            // the filter literal to the box's first coordinate.
            if (!PostgisFilterSql.TryBuild(
                filter, description.Schema, values, out var filterSql, out var filterError, startIndex: values.Count))
            {
                return new PredicateBuild(
                    IsValid: false,
                    Sql: null,
                    Parameters: [],
                    Error: PostgisDiagnostics.InvalidArgument(invocation.Capability, $"the filter is not valid: {filterError}"));
            }

            parts.Add(filterSql);
        }

        return new PredicateBuild(
            IsValid: true,
            Sql: parts.Count == 0 ? null : string.Join(" AND ", parts),
            Parameters: values,
            Error: null);
    }
}

/// <summary>The SQL predicate of one validated query, or the rejection error.</summary>
internal sealed record PredicateBuild(bool IsValid, string? Sql, IReadOnlyList<object?> Parameters, CapabilityError? Error);
