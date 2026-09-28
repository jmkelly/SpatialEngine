using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec.Tests;

/// <summary>
/// The one feature cap both upload paths share (ADR-0082 §4, ADR-0089): a
/// streamed decode has to check while reading or the cap is not a cap, and two
/// copies of that check is how they drift.
/// </summary>
public sealed class IngestPageCapTests
{
    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("geometry", AttributeKind.Geometry, false)]);

    private static Feature Point(int index) =>
        new(new FeatureId($"p{index}"), Schema, [AttributeValue.FromGeometry(GeometryFactory.CreatePoint(index, index))]);

    private static async IAsyncEnumerable<FeatureBatch> Pages(int features, int perPage)
    {
        for (var start = 0; start < features; start += perPage)
        {
            var count = Math.Min(perPage, features - start);
            yield return new FeatureBatch(Schema, Enumerable.Range(start, count).Select(Point).ToList());
        }
    }

    private static async Task<List<FeatureBatch>> DrainAsync(IAsyncEnumerable<FeatureBatch> pages)
    {
        var seen = new List<FeatureBatch>();
        await foreach (var page in pages)
        {
            seen.Add(page);
        }

        return seen;
    }

    [Fact]
    public async Task Pages_within_the_cap_pass_through_untouched()
    {
        var capped = IngestPageCap.Apply(Pages(5, 2), 10, _ => new InvalidOperationException("over the cap"));

        var drained = await DrainAsync(capped);

        Assert.Equal(3, drained.Count);
        Assert.Equal(5, drained.Sum(page => page.Count));
    }

    [Fact]
    public async Task A_stream_that_crosses_the_cap_fails_with_the_callers_own_error()
    {
        var capped = IngestPageCap.Apply(Pages(10, 2), 5, message => new ArgumentException($"esri: {message}"));

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => DrainAsync(capped));

        // The caller's own error type, carrying the one shared message.
        Assert.Equal("esri: The upload is above the configured maximum of 5 features.", failure.Message);
    }

    [Fact]
    public async Task The_cap_is_checked_while_reading_not_after_the_fact()
    {
        var read = 0;

        async IAsyncEnumerable<FeatureBatch> Counting()
        {
            await foreach (var page in Pages(100, 1))
            {
                read++;
                yield return page;
            }
        }

        var capped = IngestPageCap.Apply(Counting(), 3, _ => new InvalidOperationException("over the cap"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => DrainAsync(capped));

        // Three pages were read, not the hundred: the point of capping during
        // the stream rather than after it.
        Assert.Equal(4, read);
    }
}
