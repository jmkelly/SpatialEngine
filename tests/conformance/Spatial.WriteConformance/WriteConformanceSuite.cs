using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.WriteConformance;

/// <summary>
/// The write-path conformance suite (ADR-0037, ADR-0041, ADR-0065), the write
/// sibling of <c>QueryConformanceSuite</c>. It asserts the contract facts a
/// write is expected to honour over a small fixture, through the contract
/// faces only, so the same suite runs against every writable store:
///
/// <list type="bullet">
/// <item>an ingest creates the dataset and keys its features by the identity
/// column it reports, whether the store assigns it or the source supplied it
/// (ADR-0043, ADR-0149);</item>
/// <item>an add reports the identity the feature was stored under, and a
/// feature added with no identity is assigned one that names the row a read
/// finds (ADR-0037);</item>
/// <item>an update rewrites the row that identity names — attributes cleared
/// to null included — and a miss is a typed <c>not.found</c>, never a silent
/// success;</item>
/// <item>a delete removes the feature and reports per id, and a miss is a
/// typed <c>not.found</c>;</item>
/// <item>attachments round-trip by feature and id, are copied on the way in
/// and out, and enforce the store's quota (ADR-0065);</item>
/// <item>a dataset with no identity column rejects every edit per feature
/// rather than editing something it cannot name (ADR-0140).</item>
/// </list>
///
/// <para>
/// The comparison is deliberately whole-fact rather than per-store: a store
/// that keeps the old value when an update clears a column, keys a source
/// dataset by the decode's ordinal, or returns the stored byte array itself
/// has not honoured the contract, whatever its own integration tests assert.
/// </para>
/// </summary>
public static class WriteConformanceSuite
{
    /// <summary>Runs the whole suite against one store. The suite creates and owns its datasets; the caller owns the store.</summary>
    public static async Task RunAsync(WriteConformanceHarness harness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(harness);
        await AutoIdentityAsync(harness, cancellationToken).ConfigureAwait(false);
        await SourceIdentityAsync(harness, cancellationToken).ConfigureAwait(false);
        await AttachmentsAsync(harness, cancellationToken).ConfigureAwait(false);
        await DataOnlyDatasetAsync(harness, cancellationToken).ConfigureAwait(false);
        await CancellationAsync(harness, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An auto-identity ingest assigns a key the store owns: the source names
    /// no identity column, the outcome reports the column it chose, and every
    /// row reads back under the assigned key rather than the decode's ordinal.
    /// </summary>
    private static async Task AutoIdentityAsync(WriteConformanceHarness harness, CancellationToken cancellationToken)
    {
        var dataset = Dataset(harness);
        var outcome = await harness.Ingest
            .IngestAsync(new IngestRequest(dataset, 4326), WriteFixture.AutoPages, cancellationToken)
            .ConfigureAwait(false);

        Assert.Equal(dataset, outcome.Dataset);
        Assert.Equal(2, outcome.Features);
        Assert.Equal(4326, outcome.Srid);
        Assert.Equal(WriteFixture.AutoIdentityColumn, outcome.IdentityField);

        var stored = await ScanAsync(harness.Store, dataset, cancellationToken).ConfigureAwait(false);
        Assert.Equal(2, stored.Count);
        Assert.Equal(["1", "2"], stored.Select(feature => feature.Id.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        // The decode's provisional ids did not survive.
        Assert.DoesNotContain(stored, feature => feature.Id.Value.StartsWith("decode", StringComparison.Ordinal));

        var alpha = Require(stored, "Alpha");
        Assert.Equal(10, alpha["score"].Int64Value);
        var bravo = Require(stored, "Bravo");
        Assert.True(bravo["score"].IsNull);

        // An add with no identity is assigned one that names the stored row.
        var added = Assert.Single(await harness.Editor
            .AddAsync(dataset, new FeatureBatch(WriteFixture.AutoSchema, [WriteFixture.AutoRow(FeatureId.Unassigned, "Charlie", 30, x: 4.0)]), cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.True(added.Succeeded);
        Assert.NotEqual(FeatureId.Unassigned, added.Id);
        var charlie = await FindAsync(harness.Store, dataset, added.Id, cancellationToken).ConfigureAwait(false);
        Assert.NotNull(charlie);
        Assert.Equal("Charlie", charlie!["name"].StringValue);
        Assert.Equal(30, charlie["score"].Int64Value);

        // An update rewrites the row its identity names, including clearing a nullable column to null.
        var updated = Assert.Single(await harness.Editor
            .UpdateAsync(
                dataset,
                new FeatureBatch(WriteFixture.AutoSchema, [WriteFixture.AutoRow(alpha.Id, "Alpha prime", null, x: 1.0)]),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.True(updated.Succeeded);
        Assert.Equal(alpha.Id, updated.Id);
        var reread = await FindAsync(harness.Store, dataset, alpha.Id, cancellationToken).ConfigureAwait(false);
        Assert.NotNull(reread);
        Assert.Equal("Alpha prime", reread!["name"].StringValue);
        Assert.True(reread["score"].IsNull);

        // An update that matches no row is a typed not-found named by the id it was given.
        var miss = Assert.Single(await harness.Editor
            .UpdateAsync(
                dataset,
                new FeatureBatch(WriteFixture.AutoSchema, [WriteFixture.AutoRow(new FeatureId("999999"), "Nobody", 1, x: 1.0)]),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.False(miss.Succeeded);
        Assert.Equal(SpatialException.NotFound, miss.ErrorCode);

        // A delete removes the row and reports success; a miss reports not-found per id.
        var deleted = Assert.Single(await harness.Editor
            .DeleteAsync(dataset, [alpha.Id], cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.True(deleted.Succeeded);
        Assert.Null(await FindAsync(harness.Store, dataset, alpha.Id, cancellationToken).ConfigureAwait(false));

        var deleteMiss = Assert.Single(await harness.Editor
            .DeleteAsync(dataset, [alpha.Id], cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.False(deleteMiss.Succeeded);
        Assert.Equal(SpatialException.NotFound, deleteMiss.ErrorCode);
    }

    /// <summary>
    /// A source-identity ingest keys each feature by the identity column's own
    /// value, so a read for that value finds the row and an add stores the
    /// value it was given (ADR-0038, ADR-0112).
    /// </summary>
    private static async Task SourceIdentityAsync(WriteConformanceHarness harness, CancellationToken cancellationToken)
    {
        var dataset = Dataset(harness);
        var outcome = await harness.Ingest
            .IngestAsync(new IngestRequest(dataset, 4326, IngestIdentity.Source, "code"), WriteFixture.SourcePages, cancellationToken)
            .ConfigureAwait(false);

        Assert.Equal("code", outcome.IdentityField);
        var stored = await ScanAsync(harness.Store, dataset, cancellationToken).ConfigureAwait(false);
        Assert.Equal(["10", "13"], stored.Select(feature => feature.Id.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray());

        // An add stores the identity it was given, and the outcome names it.
        var added = Assert.Single(await harness.Editor
            .AddAsync(
                dataset,
                new FeatureBatch(WriteFixture.SourceSchema, [WriteFixture.SourceRow("20", 20, "Echo", x: 5.0)]),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.True(added.Succeeded);
        Assert.Equal("20", added.Id.Value);
        Assert.NotNull(await FindAsync(harness.Store, dataset, new FeatureId("20"), cancellationToken).ConfigureAwait(false));

        // An update by the column's value rewrites that row.
        var updated = Assert.Single(await harness.Editor
            .UpdateAsync(
                dataset,
                new FeatureBatch(WriteFixture.SourceSchema, [WriteFixture.SourceRow("13", 13, "Delta prime", x: 3.0)]),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.True(updated.Succeeded);
        var reread = await FindAsync(harness.Store, dataset, new FeatureId("13"), cancellationToken).ConfigureAwait(false);
        Assert.NotNull(reread);
        Assert.Equal("Delta prime", reread!["name"].StringValue);
    }

    /// <summary>
    /// The attachment round trip (ADR-0065): per-feature ids from one, bytes
    /// copied on read and write, an update that keeps the id, a delete that
    /// reports per id, and a quota the store enforces itself.
    /// </summary>
    private static async Task AttachmentsAsync(WriteConformanceHarness harness, CancellationToken cancellationToken)
    {
        var dataset = Dataset(harness);
        await harness.Ingest
            .IngestAsync(new IngestRequest(dataset, 4326), WriteFixture.AutoPages, cancellationToken)
            .ConfigureAwait(false);
        var feature = new FeatureId("1");
        var attachments = harness.Attachments;

        var first = await attachments
            .AddAsync(dataset, feature, new FeatureAttachmentWrite("photo.png", "image/png", [0x89, 0x50, 0x4E, 0x47], "streetscape"), cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(1, first.Id);
        Assert.Equal("photo.png", first.Name);
        Assert.Equal("image/png", first.ContentType);
        Assert.Equal(4, first.Size);
        Assert.Equal("streetscape", first.Keywords);

        Assert.Equal([first], await attachments.ListAsync(dataset, feature, cancellationToken).ConfigureAwait(false));
        Assert.Empty(await attachments.ListAsync(dataset, new FeatureId("2"), cancellationToken).ConfigureAwait(false));

        var fetched = await attachments.GetAsync(dataset, feature, first.Id, cancellationToken).ConfigureAwait(false);
        Assert.Equal(first, fetched.Descriptor);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], fetched.Content);

        // The bytes are provider-owned: mutating a read's array does not change what is stored.
        fetched.Content[0] = 0x00;
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], (await attachments.GetAsync(dataset, feature, first.Id, cancellationToken).ConfigureAwait(false)).Content);

        // A second add takes the next id, and an update replaces metadata and bytes under the same id.
        var second = await attachments
            .AddAsync(dataset, feature, new FeatureAttachmentWrite("a.bin", "", [1]), cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(2, second.Id);
        Assert.Equal("application/octet-stream", second.ContentType);

        var updated = await attachments
            .UpdateAsync(dataset, feature, first.Id, new FeatureAttachmentWrite("b.png", "image/png", [9, 10], "front"), cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal("b.png", updated.Name);
        var afterUpdate = await attachments.GetAsync(dataset, feature, first.Id, cancellationToken).ConfigureAwait(false);
        Assert.Equal(updated, afterUpdate.Descriptor);
        Assert.Equal([9, 10], afterUpdate.Content);

        // A missing attachment is a typed not-found on get and update, and a per-id failure on delete.
        Assert.Equal(
            SpatialException.NotFound,
            (await Assert.ThrowsAsync<SpatialException>(() =>
                attachments.GetAsync(dataset, feature, 999, cancellationToken)).ConfigureAwait(false)).Code);
        Assert.Equal(
            SpatialException.NotFound,
            (await Assert.ThrowsAsync<SpatialException>(() =>
                attachments.UpdateAsync(dataset, feature, 999, new FeatureAttachmentWrite("x", "", [1]), cancellationToken)).ConfigureAwait(false)).Code);

        var outcomes = await attachments.DeleteAsync(dataset, feature, [first.Id, 999], cancellationToken).ConfigureAwait(false);
        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].Succeeded);
        Assert.Equal(first.Id, outcomes[0].Id);
        Assert.False(outcomes[1].Succeeded);
        Assert.Equal(SpatialException.NotFound, outcomes[1].ErrorCode);
        Assert.Equal([second], await attachments.ListAsync(dataset, feature, cancellationToken).ConfigureAwait(false));

        // The store enforces its own byte cap: the boundary lands, one past it is rejected.
        var capped = harness.AttachmentsWithCap(4);
        await capped.AddAsync(dataset, feature, new FeatureAttachmentWrite("ok.bin", "", [1, 2, 3, 4]), cancellationToken).ConfigureAwait(false);
        Assert.Equal(
            SpatialException.InvalidArguments,
            (await Assert.ThrowsAsync<SpatialException>(() =>
                capped.AddAsync(dataset, feature, new FeatureAttachmentWrite("big.bin", "", [1, 2, 3, 4, 5]), cancellationToken)).ConfigureAwait(false)).Code);
    }

    /// <summary>
    /// A dataset created without an identity column — <c>CreateAsync</c> from a
    /// sample — cannot name the features an edit would change, so every edit
    /// reports a typed <c>invalid.arguments</c> per feature rather than
    /// changing a row it cannot address (ADR-0140).
    /// </summary>
    private static async Task DataOnlyDatasetAsync(WriteConformanceHarness harness, CancellationToken cancellationToken)
    {
        // A data-only dataset in the same store, made from a sample rather than
        // from an ingest so it declares no primary key.
        var dataOnly = Dataset(harness);
        await harness.Catalogue.CreateAsync(
            dataOnly, new FeatureBatch(WriteFixture.AutoSchema, [WriteFixture.AutoRow(new FeatureId("decode-1"), "Alpha", 10, x: 1.0)]), 4326, cancellationToken)
            .ConfigureAwait(false);

        var add = Assert.Single(await harness.Editor
            .AddAsync(dataOnly, new FeatureBatch(WriteFixture.AutoSchema, [WriteFixture.AutoRow(new FeatureId("decode-2"), "Alpha", 10, x: 1.0)]), cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.False(add.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, add.ErrorCode);

        var update = Assert.Single(await harness.Editor
            .UpdateAsync(dataOnly, new FeatureBatch(WriteFixture.AutoSchema, [WriteFixture.AutoRow(new FeatureId("1"), "Alpha", 10, x: 1.0)]), cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.False(update.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, update.ErrorCode);

        var delete = Assert.Single(await harness.Editor
            .DeleteAsync(dataOnly, [new FeatureId("1")], cancellationToken: cancellationToken)
            .ConfigureAwait(false));
        Assert.False(delete.Succeeded);
        Assert.Equal(SpatialException.InvalidArguments, delete.ErrorCode);
    }

    /// <summary>Every write face observes a cancelled token before it changes anything.</summary>
    private static async Task CancellationAsync(WriteConformanceHarness harness, CancellationToken cancellationToken)
    {
        var dataset = Dataset(harness);
        await harness.Ingest
            .IngestAsync(new IngestRequest(dataset, 4326), WriteFixture.AutoPages, cancellationToken)
            .ConfigureAwait(false);
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Editor.AddAsync(dataset, new FeatureBatch(WriteFixture.AutoSchema, [WriteFixture.AutoRow(FeatureId.Unassigned, "Charlie", 30, x: 4.0)]), cancellationToken: canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Editor.UpdateAsync(dataset, new FeatureBatch(WriteFixture.AutoSchema, [WriteFixture.AutoRow(new FeatureId("1"), "Alpha", 10, x: 1.0)]), cancellationToken: canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Editor.DeleteAsync(dataset, [new FeatureId("1")], cancellationToken: canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Attachments.AddAsync(dataset, new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "", [1]), canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Attachments.GetAsync(dataset, new FeatureId("1"), 1, canceled));
    }

    private static string Dataset(WriteConformanceHarness harness) =>
        $"{harness.SchemaName}.conformance_{Guid.NewGuid():N}";

    private static async Task<IReadOnlyList<Feature>> ScanAsync(
        IFeatureStore store, string dataset, CancellationToken cancellationToken) =>
        [.. (await store.ScanAsync(dataset, cancellationToken).ConfigureAwait(false)).SelectMany(batch => batch.Features)];

    private static async Task<Feature?> FindAsync(
        IFeatureStore store, string dataset, FeatureId id, CancellationToken cancellationToken) =>
        (await ScanAsync(store, dataset, cancellationToken).ConfigureAwait(false))
        .FirstOrDefault(feature => feature.Id.Equals(id));

    private static Feature Require(IReadOnlyList<Feature> features, string name) =>
        features.FirstOrDefault(feature => feature["name"].StringValue == name)
        ?? throw new Xunit.Sdk.XunitException($"no fixture feature named '{name}' was stored");
}
