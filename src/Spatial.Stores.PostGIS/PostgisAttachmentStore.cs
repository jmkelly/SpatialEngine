using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The PostGIS feature-attachment face (T-088, ADR-0065 §2), split
/// from <see cref="PostgisStore"/> so the store keeps one cohesive
/// read/write/transaction responsibility and the attachment sidecar owns
/// put/get/delete. Attachments persist in the provider-owned
/// <c>public.spatial_attachments</c> sidecar table — one row per attachment
/// with <c>bytea</c> content, keyed by dataset, feature identity and the
/// per-feature attachment id starting at one — so blobs survive restarts
/// alongside their datasets. Every statement is built in
/// <see cref="PostgisQueries"/> from fixed identifiers and bound
/// parameters; Npgsql types never cross the contract, which
/// carries only core and BCL types.
/// </summary>
public sealed class PostgisAttachmentStore : IFeatureAttachmentStore
{
    /// <summary>The default per-attachment byte cap (10 MiB, ADR-0065 §3).</summary>
    public const long DefaultMaxBytesPerAttachment = 10_485_760;

    private const string DefaultContentType = "application/octet-stream";

    /// <summary>Insert retries when two writers race for the same next attachment id.</summary>
    private const int MaxInsertAttempts = 3;

    private readonly PostgisStore _store;
    private readonly long _maxBytesPerAttachment;

    public PostgisAttachmentStore(PostgisStore store, long maxBytesPerAttachment = DefaultMaxBytesPerAttachment)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerAttachment);
        _store = store;
        _maxBytesPerAttachment = maxBytesPerAttachment;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureAttachmentDescriptor>> ListAsync(
        string dataset, FeatureId featureId, CancellationToken cancellationToken = default)
    {
        var name = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(async () =>
        {
            await EnsureAttachmentTableAsync(cancellationToken);
            var description = await RequireAttachmentDatasetAsync(name, cancellationToken);
            await RequireFeatureAsync(name, description, featureId, cancellationToken);
            await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
            var rows = await PostgisDataStore.ReadRowsAsync(
                connection, PostgisQueries.ListAttachments(), [name.Qualified, featureId.Value], cancellationToken);
            return (IReadOnlyList<FeatureAttachmentDescriptor>)rows.Select(MapDescriptor).ToArray();
        });
    }

    /// <inheritdoc />
    public Task<FeatureAttachmentDescriptor> AddAsync(
        string dataset, FeatureId featureId, FeatureAttachmentWrite write, CancellationToken cancellationToken = default)
    {
        ValidateUpload(write);
        var parsed = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(async () =>
        {
            await EnsureAttachmentTableAsync(cancellationToken);
            var description = await RequireAttachmentDatasetAsync(parsed, cancellationToken);
            await RequireFeatureAsync(parsed, description, featureId, cancellationToken);
            return await InsertWithRetryAsync(AttachmentWrite.For(parsed, featureId, write), 1, cancellationToken);
        });
    }

    /// <inheritdoc />
    public Task<FeatureAttachmentContent> GetAsync(
        string dataset, FeatureId featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        var name = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(async () =>
        {
            await EnsureAttachmentTableAsync(cancellationToken);
            var description = await RequireAttachmentDatasetAsync(name, cancellationToken);
            await RequireFeatureAsync(name, description, featureId, cancellationToken);
            await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
            var rows = await PostgisDataStore.ReadRowsAsync(
                connection, PostgisQueries.GetAttachment(), [name.Qualified, featureId.Value, attachmentId], cancellationToken);
            if (rows.Count == 0)
            {
                throw SpatialException.Missing($"No attachment with identity '{attachmentId}' exists.");
            }

            return MapContent(rows[0]);
        });
    }

    /// <inheritdoc />
    public Task<FeatureAttachmentDescriptor> UpdateAsync(
        string dataset, FeatureId featureId, long attachmentId, FeatureAttachmentWrite write,
        CancellationToken cancellationToken = default)
    {
        ValidateUpload(write);
        var parsed = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(async () =>
        {
            await EnsureAttachmentTableAsync(cancellationToken);
            var description = await RequireAttachmentDatasetAsync(parsed, cancellationToken);
            await RequireFeatureAsync(parsed, description, featureId, cancellationToken);
            await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
            var resolved = OrDefaultContentType(write.ContentType);
            var affected = await PostgisDataStore.ExecuteNonQueryAsync(
                connection,
                PostgisQueries.UpdateAttachment(),
                [parsed.Qualified, featureId.Value, attachmentId, write.Name, resolved, (long)write.Content.Length,
                    write.Keywords, write.Content],
                cancellationToken);
            if (affected == 0)
            {
                throw SpatialException.Missing($"No attachment with identity '{attachmentId}' exists.");
            }

            return new FeatureAttachmentDescriptor(attachmentId, write.Name, resolved, write.Content.Length, write.Keywords);
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FeatureAttachmentOutcome>> DeleteAsync(
        string dataset, FeatureId featureId, IReadOnlyList<long> attachmentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        var name = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        return _store.RunStoreOperationAsync(async () =>
        {
            await EnsureAttachmentTableAsync(cancellationToken);
            var description = await RequireAttachmentDatasetAsync(name, cancellationToken);
            await RequireFeatureAsync(name, description, featureId, cancellationToken);
            await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
            var outcomes = new List<FeatureAttachmentOutcome>(attachmentIds.Count);
            foreach (var attachmentId in attachmentIds)
            {
                var affected = await PostgisDataStore.ExecuteNonQueryAsync(
                    connection,
                    PostgisQueries.DeleteAttachment(),
                    [name.Qualified, featureId.Value, attachmentId],
                    cancellationToken);
                outcomes.Add(DeleteOutcome(attachmentId, affected));
            }

            return (IReadOnlyList<FeatureAttachmentOutcome>)outcomes;
        });
    }

    private async Task<FeatureAttachmentDescriptor> InsertWithRetryAsync(
        AttachmentWrite write, int attempt, CancellationToken cancellationToken)
    {
        var resolved = OrDefaultContentType(write.ContentType);
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
        var nextId = await NextAttachmentIdAsync(connection, write, cancellationToken);
        try
        {
            await PostgisDataStore.ExecuteNonQueryAsync(
                connection,
                PostgisQueries.InsertAttachment(),
                [write.Dataset.Qualified, write.FeatureId.Value, nextId, write.Name, resolved,
                    (long)write.Content.Length, write.Keywords, write.Content],
                cancellationToken);
            return new FeatureAttachmentDescriptor(nextId, write.Name, resolved, write.Content.Length, write.Keywords);
        }
        catch (PostgresException exception) when (IsRacedInsert(exception))
        {
            return await RetryInsertAsync(write, attempt, exception, cancellationToken);
        }
    }

    /// <summary>Re-reads the high-water mark for the next attempt, or rethrows once the attempts are spent.</summary>
    private async Task<FeatureAttachmentDescriptor> RetryInsertAsync(
        AttachmentWrite write, int attempt, PostgresException failure, CancellationToken cancellationToken)
    {
        if (attempt >= MaxInsertAttempts)
        {
            throw failure;
        }

        // Two writers read the same high-water mark; re-read it and try the next id.
        return await InsertWithRetryAsync(write, attempt + 1, cancellationToken);
    }

    private static bool IsRacedInsert(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UniqueViolation;

    private static async Task<long> NextAttachmentIdAsync(
        NpgsqlConnection connection, AttachmentWrite write, CancellationToken cancellationToken)
    {
        var marks = await PostgisDataStore.ReadRowsAsync(
            connection, PostgisQueries.MaxAttachmentId(), [write.Dataset.Qualified, write.FeatureId.Value], cancellationToken);
        return Convert.ToInt64(marks[0][0], System.Globalization.CultureInfo.InvariantCulture) + 1;
    }

    private async Task EnsureAttachmentTableAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
        await PostgisDataStore.ExecuteNonQueryAsync(
            connection, PostgisQueries.EnsureAttachmentTable(), [], cancellationToken);
    }

    private async Task<DatasetDescription> RequireAttachmentDatasetAsync(
        PostgisDatasetName name, CancellationToken cancellationToken)
    {
        var description = await _store.DescribeInternalAsync(name, cancellationToken);
        if (description.IdColumns.Count == 0)
        {
            throw SpatialException.BadArguments(
                $"The dataset '{name}' has no primary key, so attachments are unsupported.");
        }

        return description;
    }

    private async Task RequireFeatureAsync(
        PostgisDatasetName name, DatasetDescription description, FeatureId featureId, CancellationToken cancellationToken)
    {
        var values = PostgisIdentity.Values(description, featureId);
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
        var rows = await PostgisDataStore.ReadRowsAsync(
            connection,
            PostgisQueries.FeatureExists(
                name, description.Schema, description.IdColumns, await _store.ByteOrderTextAsync(description, cancellationToken)),
            values,
            cancellationToken);
        if (rows.Count == 0)
        {
            throw SpatialException.Missing($"No feature with identity '{featureId}' exists in dataset '{name}'.");
        }
    }

    /// <summary>One attachment write: the resolved target and payload, independent of the attempt that runs it.</summary>
    private sealed record AttachmentWrite(
        PostgisDatasetName Dataset,
        FeatureId FeatureId,
        string Name,
        string ContentType,
        byte[] Content,
        string? Keywords)
    {
        public static AttachmentWrite For(PostgisDatasetName dataset, FeatureId featureId, FeatureAttachmentWrite write) =>
            new(dataset, featureId, write.Name, write.ContentType, write.Content, write.Keywords);
    }

    /// <summary>The one guard every attachment write shares: a named, non-empty body inside the per-attachment quota.</summary>
    private void ValidateUpload(FeatureAttachmentWrite write)
    {
        if (string.IsNullOrWhiteSpace(write.Name))
        {
            throw SpatialException.BadArguments("An attachment name is required.");
        }

        if (write.Content is null)
        {
            throw SpatialException.BadArguments("Attachment content is required.");
        }

        if (write.Content.Length > _maxBytesPerAttachment)
        {
            throw SpatialException.BadArguments(
                $"Attachment '{write.Name}' is {write.Content.Length} bytes, over the {_maxBytesPerAttachment}-byte per-attachment quota.");
        }
    }

    private static string OrDefaultContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? DefaultContentType : contentType;

    private static FeatureAttachmentDescriptor MapDescriptor(IReadOnlyList<object?> row) =>
        new(
            Convert.ToInt64(row[0], System.Globalization.CultureInfo.InvariantCulture),
            (string)row[1]!,
            (string)row[2]!,
            Convert.ToInt64(row[3], System.Globalization.CultureInfo.InvariantCulture),
            row[4] is DBNull ? null : (string?)row[4]);

    /// <summary>The per-id outcome of a delete: success, or a typed not-found failure.</summary>
    private static FeatureAttachmentOutcome DeleteOutcome(long attachmentId, int affected) =>
        affected > 0
            ? FeatureAttachmentOutcome.Success(attachmentId)
            : FeatureAttachmentOutcome.Failure(
                attachmentId, SpatialException.NotFound, $"No attachment with identity '{attachmentId}' exists.");

    private static FeatureAttachmentContent MapContent(IReadOnlyList<object?> row) =>
        new(MapDescriptor(row), (byte[])row[5]!);
}
