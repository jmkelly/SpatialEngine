using System.Globalization;
using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The SQL Server plan read (ADR-0074 §4, ADR-0116 §1, ADR-0124 §1): the
/// restriction, the order and the page are compiled to T-SQL and read by the
/// database, and the page is finished here — the batches, whether more
/// remains, the continuation cursor and the total, which the count statement
/// already answered. A plan whose order T-SQL cannot reproduce the reference's
/// is not this reader's: it returns <c>null</c> and the store finishes that
/// plan with the shared reference executor over the rows it selected, which is
/// what the store did before the page was pushed (ADR-0124 §6-§7).
///
/// <para>
/// The rows that cross the wire differ from the fallback's; the answer does
/// not. Both the pushed page and the reference page carry the same total, the
/// same sequence in the same order, and a cursor the plan can continue from,
/// which is what the conformance suite measures.
/// </para>
/// </summary>
internal sealed class SqlServerPlanReader(SqlServerStorage storage, SqlServerCatalogue catalogue)
{
    private const int BatchSize = 512;

    /// <summary>
    /// Reads the page a plan names, or answers <c>null</c> when this dialect
    /// cannot address it — the seam where the store keeps the reference
    /// executor (ADR-0116 §1, ADR-0124 §6).
    /// </summary>
    public async Task<FeatureQueryPage?> ReadAsync(
        SqlServerDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        var schema = (FeatureSchema)description.Schema;
        FeatureQueryValidation.Validate(schema, query);
        if (query.Order is not { Count: > 0 })
        {
            // An unordered plan is never paged here (see `Pushed`), and this is
            // the check that costs no round trip: the collation probe below is
            // one, and a plan this rejects has no use for it.
            return null;
        }

        var byteOrderText = SqlServerTextCollation.IsByteOrder(
            await storage.DatabaseCollationAsync(cancellationToken));
        var order = SqlServerPlanQueries.Order(
            query.Order, description.IdColumns, description.Schema, byteOrderText);
        if (!Pushed(query, order))
        {
            return null;
        }

        var parameters = new List<object?>();
        var where = Predicate(description, query, parameters);
        var start = FeaturePageCursor.StartOffset(query);
        var total = (int)await ScalarAsync(name, SqlServerPlanQueries.Count(name, where), parameters, cancellationToken);
        var shape = Columns(description, query.Projection);
        var page = await BatchesAsync(
            name,
            SqlServerPlanQueries.Read(name, shape.Columns, where, order, new SqlServerPlanQueries.Paging(query.Limit, start), parameters),
            parameters,
            shape,
            description,
            cancellationToken);
        var consumed = page.Sum(batch => batch.Count);
        var more = start + consumed < total;
        return FeatureQueryPage.Page(
            page,
            more,
            more ? FeaturePageCursor.Issue(query, start + consumed) : null,
            total);
    }

    /// <summary>
    /// Whether this dialect can address the plan's page in T-SQL
    /// (ADR-0116 §1, ADR-0124 §6-§7). It can when the plan's order is one this
    /// table can make total — the identity tie-break included — because
    /// <c>OFFSET</c>/<c>FETCH NEXT</c> is legal in T-SQL only over an
    /// <c>ORDER BY</c>, and a row's place in a page is its place in an order.
    ///
    /// <para>
    /// A plan that asked for no order is therefore finished in process even
    /// though its restriction was pushed. That is one place where this store is
    /// narrower than the PostGIS one, for a dialect reason rather than a
    /// choice: an <c>OFFSET</c> needs an <c>ORDER BY</c> to skip over, and the
    /// only order T-SQL would accept there is one this store would be inventing
    /// — an unordered plan's order is its scan order, and ordering a page by
    /// the primary key would return a sequence the reference never produced.
    /// </para>
    ///
    /// <para>
    /// A plan that asked for <em>several</em> keys is pushed, and each key is a
    /// tie-break over the ones before it: that is the order the reference
    /// computes (ADR-0127) and the order this dialect writes, so the two agree
    /// on a composite plan as they always agreed on a single-key one. It was
    /// finished in process while the reference applied each key with a fresh
    /// <c>OrderBy</c> and so computed the <em>last</em> key's order
    /// (SpatialEngine-u2x.54, which fixed the reference); the temporary
    /// accommodation is gone.
    /// </para>
    /// </summary>
    internal static bool Pushed(FeatureQuery query, IReadOnlyList<string>? order) => order is not null;

    /// <summary>
    /// The restriction a plan pushes into the <c>WHERE</c>: the identity
    /// restriction, the bounding-box pre-filter and the attribute predicate,
    /// all as bound values over discovered identifiers, compiled by the same
    /// <see cref="SqlServerPredicateSql"/> the scan path uses so a plan and a
    /// filter cannot drift into two different answers.
    /// </summary>
    private static string? Predicate(DatasetDescription description, FeatureQuery query, List<object?> parameters)
    {
        var identity = Identity(description, query.Ids, parameters);
        var restriction = SqlServerPredicateSql.Build(description, query.BoundingBox, query.Where, parameters);
        return (identity, restriction) switch
        {
            (null, null) => null,
            (null, _) => restriction,
            (_, null) => identity,
            _ => $"({identity}) AND ({restriction})",
        };
    }

    /// <summary>
    /// The identity restriction as one OR-group per requested tuple, one bound
    /// parameter per identity value, in input order (ADR-0038). An empty set of
    /// ids selects nothing, and a table with no identity column cannot be
    /// restricted by identity at all — which cannot arise here, because a plan
    /// this reader takes has an identity tie-break to make its order total.
    /// </summary>
    private static string? Identity(
        DatasetDescription description, IReadOnlyList<FeatureId>? ids, List<object?> parameters)
    {
        if (ids is null || description.IdColumns.Count == 0)
        {
            return null;
        }

        if (ids.Count == 0)
        {
            return "(1 = 0)";
        }

        var groups = ids
            .Select(id => string.Join(
                " AND ",
                description.IdColumns.Select((column, position) =>
                    $"{SqlServerIdentifier.Quote(column)} = {Parameter(parameters, SqlServerIdentity.Values(description, id)[position])}")))
            .ToArray();
        return groups.Length == 1 ? groups[0] : "(" + string.Join(" OR ", groups) + ")";
    }

    private static string Parameter(List<object?> parameters, object? value)
    {
        parameters.Add(value);
        return $"@p{parameters.Count - 1}";
    }

    /// <summary>
    /// How a pushed read is shaped in T-SQL: the plan's projection as the
    /// result schema, plus the identity columns the row mapper needs to name
    /// each row, and the column list that reads both. The identity columns are
    /// dropped again before the page is returned, so a projection returns
    /// exactly the fields it asked for.
    /// </summary>
    private static Shape Columns(DatasetDescription description, IReadOnlyList<string>? projection)
    {
        var fields = (projection is null or { Count: 0 }
            ? description.Schema.Fields
            : projection
                .Where(field => field != AggregateSpec.AllFields)
                .Select(field => description.Schema[description.Schema.IndexOf(field)])
                .ToArray()).ToList();
        var result = new FeatureSchema(fields);
        var read = new FeatureSchema([
            .. fields,
            .. description.IdColumns
                .Select(column => description.Schema[description.Schema.IndexOf(column)])
                .Where(field => !fields.Contains(field))]);
        // The identity indexes are the identity columns' positions in what was
        // *read*, which is not the set of columns that were appended: a read
        // that carries the whole schema already carries them, and taking the
        // appended ones as the identity would name every feature by its row
        // ordinal instead of its key.
        var indexes = description.IdColumns.Select(column => read.IndexOf(column)).Where(index => index >= 0).ToArray();
        return new Shape(SqlServerPlanQueries.Columns(read, null), read, result, indexes);
    }

    private async Task<FeatureBatch[]> BatchesAsync(
        SqlServerDatasetName name,
        string sql,
        IReadOnlyList<object?> parameters,
        Shape shape,
        DatasetDescription description,
        CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        await using var reader = await SqlServerDataStore.ExecuteReaderAsync(connection, sql, parameters, cancellationToken);
        var features = new List<Feature>(BatchSize);
        long ordinal = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = SqlServerDataStore.ReadRow(reader, reader.FieldCount);
            features.Add(shape.Project(
                shape.Read,
                SqlServerRowMapper.MapRow(shape.Read, shape.Identity, row, ordinal++, description.Srid)));
        }

        return features.Count == 0
            ? [new FeatureBatch(shape.Result, [])]
            : features.Chunk(BatchSize).Select(chunk => new FeatureBatch(shape.Result, chunk)).ToArray();
    }

    private async Task<long> ScalarAsync(
        SqlServerDatasetName name, string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await SqlServerDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        return rows.Count == 0
            ? throw SpatialException.Unavailable("The database returned no row for a single-row aggregate query.")
            : Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);
    }

    /// <summary>One pushed-down read's shape: what is read, and what is returned.</summary>
    private sealed record Shape(
        IReadOnlyList<string> Columns,
        FeatureSchema Read,
        FeatureSchema Result,
        IReadOnlyList<int> Identity)
    {
        /// <summary>Drops the identity columns a read needed and keeps the plan's projection.</summary>
        public Feature Project(FeatureSchema read, Feature feature)
        {
            var values = new AttributeValue[Result.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = ReferenceEquals(read, Result) ? feature[i] : feature[read.IndexOf(Result[i].Name)];
            }

            return new Feature(feature.Id, Result, values);
        }
    }
}
