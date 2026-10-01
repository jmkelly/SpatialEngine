using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// What a plan read over a whole dataset costs in the reference executor, and
/// that the page it returns is the page a full sort would return.
///
/// <para>
/// A store that cannot push a plan down reads everything and hands it here
/// (ADR-0074 §4, ADR-0097 §1, ADR-0116 §1): on a dataset that declares no
/// identity column that is the documented answer, not a leak. What is not the
/// answer is paying for the whole read twice — the baseline spike measured a
/// capped read of a 34,135-row keyless layer at 46.7 MB against 31.1 MB for a
/// plain scan of the same table (SpatialEngine-yup), and the whole 15 MB
/// residual was the executor narrowing a whole read it had already paid for.
///
/// <para>
/// A page of 25 rows out of 34,135 needs 25 rows' worth of order, not 34,135
/// rows' worth: the executor's order is a bounded selection over the page's
/// window rather than a sort of everything, and the tests here pin that the
/// window is the same total order the full sort produces — every key, the
/// identity tie-break, and the read's own order behind them.
/// </para>
/// <para>
/// The measurements run in their own collection: allocation is counted for the
/// whole process, so a figure is this test's only when nothing else in the
/// assembly is allocating.
/// </para>
/// </summary>
[Collection(FeaturePlanExecutorAllocationTests.Alone.Name)]
public sealed class FeaturePlanExecutorAllocationTests
{
    private const int Rows = 34_135;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("country", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
    ]);

    /// <summary>
    /// A layer's worth of rows, keyed so that ties are common: the population
    /// repeats every 1,000 rows and the identity tie-break decides them.
    /// </summary>
    private static List<Feature> Layer()
    {
        var rows = new List<Feature>(Rows);
        for (var i = 0; i < Rows; i++)
        {
            rows.Add(new Feature(
                new FeatureId(i.ToString(CultureInfo.InvariantCulture)),
                Schema,
                [
                    AttributeValue.FromString("city " + (i % 7_000).ToString(CultureInfo.InvariantCulture)),
                    AttributeValue.FromString("country " + (i % 200).ToString(CultureInfo.InvariantCulture)),
                    AttributeValue.FromInt64(i % 1_000),
                ]));
        }

        return rows;
    }

    /// <summary>Allocation of one call, warm — the second call is the same shape.</summary>
    private static long Allocated(Func<FeatureQueryPage> call)
    {
        call();
        call();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        call();
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    /// <summary>
    /// A capped plan over a whole read allocates what a page costs, not what a
    /// whole set's order costs: the executor holds the page's keys and the
    /// read's references, and neither grows with the read.
    ///
    /// <para>
    /// The bound is a ratio against the same plan answered without an order,
    /// because that is the read plus its list copies and nothing else — the
    /// floor any page costs. A full sort of every row allocates a key and an
    /// index per row per key, which on this layer is more than twice that
    /// floor; a bounded selection over a 25-row page is a 25-element heap.
    /// </para>
    /// </summary>
    [Fact]
    public void A_capped_page_over_a_whole_read_costs_a_page_and_not_a_sort_of_the_read()
    {
        var rows = Layer();

        var floor = Allocated(() => FeaturePlanExecutor.Execute(Schema, rows, new FeatureQuery(Limit: 25)));
        var ordered = Allocated(() => FeaturePlanExecutor.Execute(
            Schema, rows, new FeatureQuery(Order: [new OrderTerm("population")], Limit: 25)));

        Assert.True(
            ordered < floor * 2,
            $"an ordered 25-row page over {Rows} rows allocated {ordered} bytes against {floor} for the same page unordered; "
            + "a page of 25 rows cannot cost more than the read that produced it.");
    }

    /// <summary>
    /// Every page of a walk costs what its own window costs and not what the
    /// read costs: what a page at offset 33,000 has to compare is 33,025 rows,
    /// not the 25 it answers with, but it is still a bounded selection over
    /// the rows in front of it rather than the sorted read the reference always
    /// paid for. The uncapped ordered read over the same rows is that cost, and
    /// it is the figure the 34,135-row keyless layer was measured against.
    /// </summary>
    [Fact]
    public void Every_page_of_a_walk_costs_its_window_and_not_the_whole_read()
    {
        var rows = Layer();
        var plan = new FeatureQuery(Order: [new OrderTerm("population")], Limit: 25);

        var wholeRead = Allocated(() => FeaturePlanExecutor.Execute(Schema, rows, plan with { Limit = null, Offset = null }));

        foreach (var offset in new[] { 0, 1_000, 33_000 })
        {
            var page = Allocated(() => FeaturePlanExecutor.Execute(Schema, rows, plan with { Offset = offset }));
            Assert.True(
                page < wholeRead / 2,
                $"a 25-row page at offset {offset} allocated {page} bytes; ordering the whole {Rows}-row read costs {wholeRead}.");
        }
    }

    /// <summary>
    /// The bounded selection is the full sort, window for window: the same
    /// total order — requested keys in sequence, the identity tie-break, and
    /// the read's own order behind both — over every window of it. The oracle
    /// here is an independent sort written in the test, so a change to the
    /// executor's ordering is caught even where it is self-consistent.
    /// </summary>
    [Fact]
    public void A_window_of_the_bounded_order_is_the_window_of_the_full_order()
    {
        var rows = Layer();
        var expected = FullOrder(rows, [new OrderTerm("population"), new OrderTerm("name", SortDirection.Descending)]);

        foreach (var (offset, limit) in new[] { (0, 25), (1, 25), (999, 25), (Rows - 3, 25), (12, 1), (0, 500), (7, (int?)null) })
        {
            var page = FeaturePlanExecutor.Execute(
                Schema, rows, new FeatureQuery(
                    Order: [new OrderTerm("population"), new OrderTerm("name", SortDirection.Descending)],
                    Offset: offset,
                    Limit: limit));

            var taken = Math.Min(limit ?? Rows, Math.Max(expected.Count - offset, 0));
            Assert.Equal([.. expected.Skip(offset).Take(taken)], page.Batches.SelectMany(batch => batch.Features).Select(feature => feature.Id.Value));
        }
    }

    /// <summary>The same order, computed without the executor.</summary>
    private static List<string> FullOrder(List<Feature> rows, OrderTerm[] terms)
    {
        var ascending = new ValueComparer(reverse: false);
        var population = Schema.IndexOf(terms[0].Field);
        var name = Schema.IndexOf(terms[1].Field);
        var ordered = rows
            .Select((feature, index) => (feature, index))
            .OrderBy(pair => pair.feature[population], terms[0].IsDescending ? new ValueComparer(reverse: true) : ascending)
            .ThenBy(pair => pair.feature[name], terms[1].IsDescending ? new ValueComparer(reverse: true) : ascending)
            .ThenBy(pair => pair.feature.Id.Value, StringComparer.Ordinal)
            .ThenBy(pair => pair.index);

        return [.. ordered.Select(pair => pair.feature.Id.Value)];
    }

    /// <summary>The engine's value order, optionally reversed — an oracle written apart from the executor's own.</summary>
    private sealed class ValueComparer(bool reverse) : IComparer<AttributeValue>
    {
        public int Compare(AttributeValue left, AttributeValue right)
        {
            var order = AttributeValueComparer.Instance.Compare(left, right);
            return reverse ? -order : order;
        }
    }

    /// <summary>
    /// A collection of its own, so an allocation figure is not another test's:
    /// <see cref="GC.GetTotalAllocatedBytes"/> counts the process, and the
    /// other classes in this assembly run in parallel with any test that is not
    /// in a collection that forbids it.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class Alone
    {
        public const string Name = "allocation";
    }
}