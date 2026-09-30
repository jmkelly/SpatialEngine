using Spatial.Contracts.Providers;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// What one catalogue read discovered about a dataset: the description the
/// contract carries, and the collations its columns declare for themselves
/// (ADR-0122, ADR-0136).
///
/// <para>
/// The collations are provider facts and stay inside the provider — a
/// <see cref="DatasetDescription"/> is served as JSON by
/// <c>GET /api/datasets/{id}</c> and is written in core vocabulary only, so the
/// column-collations map is held beside the description rather than in it. It
/// is discovered in the same read and dropped in the same
/// <see cref="PostgisDescriptionCache.Invalidate"/>, because a collation that
/// outlived the schema it was read for would answer for a table that is no
/// longer there.
/// </para>
/// </summary>
/// <param name="Description">The dataset as the contract describes it.</param>
/// <param name="TextCollations">
/// The collations the columns declare themselves, keyed by column name. The
/// columns that declare none are absent: those are the database's, and the
/// store reads that separately (ADR-0121).
/// </param>
internal readonly record struct PostgisDatasetFacts(
    PostgisDatasetName Dataset,
    DatasetDescription Description,
    IReadOnlyDictionary<string, string> TextCollations);
