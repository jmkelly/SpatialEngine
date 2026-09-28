using System.Runtime.CompilerServices;
using Spatial.Core.Features;

namespace Spatial.Ingest.Codec;

/// <summary>
/// The feature cap on a streamed decode (ADR-0082 §4, ADR-0089). One helper,
/// because the neutral and Esri upload paths both enforce it and two copies of
/// a cap are two caps that drift.
/// <para>
/// The cap is checked <em>while</em> the pages arrive: a buffered decode can
/// count what it read, but a streamed one that checks afterwards has already
/// read the whole upload, so the cap would be no cap at all.
/// </para>
/// </summary>
public static class IngestPageCap
{
    /// <summary>The message both upload paths answer with, so a caller sees one wording.</summary>
    public static string Exceeded(long maxFeatures) =>
        $"The upload is above the configured maximum of {maxFeatures} features.";

    /// <summary>
    /// Wraps <paramref name="pages"/> so reading fails — with the caller's own
    /// error type — as soon as more than <paramref name="maxFeatures"/>
    /// features have been read.
    /// </summary>
    public static IAsyncEnumerable<FeatureBatch> Apply(
        IAsyncEnumerable<FeatureBatch> pages,
        long maxFeatures,
        Func<string, Exception> onExceeded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(onExceeded);
        return Capped(pages, maxFeatures, onExceeded, cancellationToken);
    }

    private static async IAsyncEnumerable<FeatureBatch> Capped(
        IAsyncEnumerable<FeatureBatch> pages,
        long maxFeatures,
        Func<string, Exception> onExceeded,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long seen = 0;
        await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            seen += page.Count;
            if (seen > maxFeatures)
            {
                throw onExceeded(Exceeded(maxFeatures));
            }

            yield return page;
        }
    }
}
