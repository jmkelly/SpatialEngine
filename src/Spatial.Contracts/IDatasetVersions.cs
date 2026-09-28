namespace Spatial.Contracts;

using Spatial.Contracts.Providers;

/// <summary>
/// A feature store that reports a content version for a dataset (ADR-0075).
/// The version is an opaque token that changes whenever the dataset's feature
/// content changes, so a derived cache keyed by it — the tile cache
/// (ADR-0046/0070) — invalidates exactly the datasets an edit touched,
/// without a cache scan and without the caller knowing the content. It is not
/// an existence check and not a data value: callers fold it into a key and
/// never parse it. A store that does not implement this face reports
/// <see cref="ContentVersions.OfAsync"/> and keeps its previous behaviour.
/// </summary>
public interface IVersionedFeatureStore
{
    /// <summary>The dataset's current content version, as an opaque token.</summary>
    ValueTask<string> GetContentVersionAsync(string dataset, CancellationToken cancellationToken = default);
}

/// <summary>
/// The content-version vocabulary (ADR-0075): the token reported for a
/// dataset whose store exposes no content version, the one resolution path
/// that asks a store for its version without the caller testing for the
/// optional face, and the fold that turns a request's datasets into the single
/// token a derived cache keys on. A versioned store reports its own token, so
/// a store that starts versioning simply changes the key it produces.
/// </summary>
public static class ContentVersions
{
    /// <summary>The token reported for a dataset whose store exposes no content version.</summary>
    public const string Unversioned = "0";

    /// <summary>
    /// The store's content version for <paramref name="dataset"/>, or
    /// <see cref="Unversioned"/> when it exposes no version, so the tile key
    /// path has one code path instead of a per-store branch.
    /// </summary>
    public static ValueTask<string> OfAsync(IFeatureStore store, string dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store is IVersionedFeatureStore versioned
            ? versioned.GetContentVersionAsync(dataset, cancellationToken)
            : ValueTask.FromResult(Unversioned);
    }

    /// <summary>
    /// Folds the content versions of every dataset a render reads into one
    /// opaque token, so a cache keyed by it misses exactly when one of those
    /// datasets changed (ADR-0075). Order matters (the same request must fold
    /// the same way); the token is a hash, so callers can neither read nor
    /// forge it.
    /// </summary>
    public static async Task<string> FoldAsync(
        IStoreRegistry stores,
        IReadOnlyList<ContentVersionRef> datasets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(datasets);
        var parts = new List<string>(datasets.Count);
        foreach (var reference in datasets)
        {
            var version = await OfAsync(stores.Features(reference.Store), reference.Dataset, cancellationToken).ConfigureAwait(false);
            parts.Add(reference.Store + Separator + reference.Dataset + Separator + version);
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join(Separator, parts))));
    }

    private const char Separator = '\u001f';
}

/// <summary>One dataset a render reads, and the store that serves it (ADR-0075).</summary>
/// <param name="Store">The keyed store the dataset is read from.</param>
/// <param name="Dataset">The dataset identity within that store.</param>
public readonly record struct ContentVersionRef(string Store, string Dataset);
