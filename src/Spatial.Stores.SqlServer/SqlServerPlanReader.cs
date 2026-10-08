using Spatial.Contracts;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;

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
///
/// <para>
/// The faces live in three focused readers this type composes: the page read
/// (<see cref="SqlServerPageReader"/>), the restriction it pushes
/// (<see cref="SqlServerRestrictionBuilder"/>), and the reductions
/// (<see cref="SqlServerReductionReader"/>).
/// </para>
/// </summary>
internal sealed class SqlServerPlanReader(SqlServerStorage storage, SqlServerCatalogue catalogue)
{
    /// <summary>
    /// Reads the page a plan names, or answers <c>null</c> when this dialect
    /// cannot address it — the seam where the store keeps the reference
    /// executor (ADR-0116 §1, ADR-0124 §6).
    /// </summary>
    public Task<FeatureQueryPage?> ReadAsync(
        SqlServerDatasetName name, FeatureQuery query, CancellationToken cancellationToken) =>
        new SqlServerPageReader(storage, catalogue).ReadAsync(name, query, cancellationToken);

    /// <summary>
    /// Whether this dialect can address the plan's page in T-SQL
    /// (ADR-0116 §1, ADR-0124 §6-§7). It can when the plan's order is one this
    /// table can make total — the identity tie-break included.
    /// </summary>
    internal static bool Pushed(FeatureQuery query, IReadOnlyList<string>? order) =>
        SqlServerPageReader.Pushed(query, order);

    /// <summary>
    /// The count of the rows a plan selects, counted by the database over the
    /// restriction it pushed (ADR-0133 §2).
    /// </summary>
    public Task<int> CountAsync(
        SqlServerDatasetName name, FeatureQuery query, CancellationToken cancellationToken) =>
        new SqlServerReductionReader(storage, catalogue).CountAsync(name, query, cancellationToken);

    /// <summary>
    /// The deduplicated field combinations a plan selects, pushed as a
    /// <c>SELECT DISTINCT</c> when the plan's order is total over them, and
    /// answered <c>null</c> when it is not (ADR-0133 §6).
    /// </summary>
    public Task<DistinctPage?> DistinctAsync(
        SqlServerDatasetName name, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken) =>
        new SqlServerReductionReader(storage, catalogue).DistinctAsync(name, query, distinct, cancellationToken);

    /// <summary>
    /// The grouped reduction a plan selects, pushed as a <c>GROUP BY</c> when
    /// T-SQL can return the reference's group order and answer every statistic
    /// the request names, and answered <c>null</c> when it cannot (ADR-0128 §8,
    /// ADR-0133 §4).
    /// </summary>
    public Task<AggregatePage?> AggregateAsync(
        SqlServerDatasetName name, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken) =>
        new SqlServerReductionReader(storage, catalogue).AggregateAsync(name, query, aggregate, cancellationToken);
}
