using Npgsql;
using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
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
    public async Task<IReadOnlyList<FeatureAttachmentDescriptor>> ListAsync(
        string dataset, FeatureId featureId, CancellationToken cancellationToken = default)
    {
        var name = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        try
        {
            await EnsureAttachmentTableAsync(cancellationToken);
            var description = await RequireAttachmentDatasetAsync(name, cancellationToken);
            await RequireFeatureAsync(name, description, featureId, cancellationToken);
            await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
            var rows = await PostgisDataStore.ReadRowsAsync(
                connection, PostgisQueries.ListAttachments(), [name.Qualified, featureId.Value], cancellationToken);
            return rows.Select(MapDescriptor).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpatialException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw _store.StoreFailure(exception);
        }
    }

    /// <inheritdoc />
    public async Task<FeatureAttachmentDescriptor> AddAsync(
        string dataset, FeatureId featureId, string name, string contentType, byte[] content,
        string? keywords = null, CancellationToken cancellationToken = default)
    {
        RequireName(name);
        RequireContent(content);
        CheckQuota(name, content.Length);
        var parsed = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        try
        {
            await EnsureAttachmentTableAsync(cancellationToken);
            var description = await RequireAttachmentDatasetAsync(parsed, cancellationToken);
            await RequireFeatureAsync(parsed, description, featureId, cancellationToken);
            return await InsertWithRetryAsync(parsed, featureId, name, contentType, content, keywords, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpatialException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw _store.StoreFailure(exception);
        }
    }

    /// <inheritdoc />
    public async Task<FeatureAttachmentContent> GetAsync(
        string dataset, FeatureId featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        var name = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        try
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpatialException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw _store.StoreFailure(exception);
        }
    }

    /// <inheritdoc />
    public async Task<FeatureAttachmentDescriptor> UpdateAsync(
        string dataset, FeatureId featureId, long attachmentId, string name, string contentType, byte[] content,
        string? keywords = null, CancellationToken cancellationToken = default)
    {
        RequireName(name);
        RequireContent(content);
        CheckQuota(name, content.Length);
        var parsed = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        try
        {
            await EnsureAttachmentTableAsync(cancellationToken);
            var description = await RequireAttachmentDatasetAsync(parsed, cancellationToken);
            await RequireFeatureAsync(parsed, description, featureId, cancellationToken);
            await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
            var resolved = OrDefaultContentType(contentType);
            var affected = await PostgisDataStore.ExecuteNonQueryAsync(
                connection,
                PostgisQueries.UpdateAttachment(),
                [parsed.Qualified, featureId.Value, attachmentId, name, resolved, (long)content.Length, keywords, content],
                cancellationToken);
            if (affected == 0)
            {
                throw SpatialException.Missing($"No attachment with identity '{attachmentId}' exists.");
            }

            return new FeatureAttachmentDescriptor(attachmentId, name, resolved, content.Length, keywords);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpatialException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw _store.StoreFailure(exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureAttachmentOutcome>> DeleteAsync(
        string dataset, FeatureId featureId, IReadOnlyList<long> attachmentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        var name = PostgisStore.ParseDataset(dataset);
        cancellationToken.ThrowIfCancellationRequested();
        _store.RequireConfigured();
        try
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
                outcomes.Add(affected > 0
                    ? FeatureAttachmentOutcome.Success(attachmentId)
                    : FeatureAttachmentOutcome.Failure(
                        attachmentId, SpatialException.NotFound, $"No attachment with identity '{attachmentId}' exists."));
            }

            return outcomes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SpatialException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw _store.StoreFailure(exception);
        }
    }

    private async Task<FeatureAttachmentDescriptor> InsertWithRetryAsync(
        PostgisDatasetName name, FeatureId featureId, string attachmentName, string contentType,
        byte[] content, string? keywords, CancellationToken cancellationToken)
    {
        var resolved = OrDefaultContentType(contentType);
        for (var attempt = 0; ; attempt++)
        {
            await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
            var marks = await PostgisDataStore.ReadRowsAsync(
                connection, PostgisQueries.MaxAttachmentId(), [name.Qualified, featureId.Value], cancellationToken);
            var nextId = Convert.ToInt64(marks[0][0], System.Globalization.CultureInfo.InvariantCulture) + 1;
            try
            {
                await PostgisDataStore.ExecuteNonQueryAsync(
                    connection,
                    PostgisQueries.InsertAttachment(),
                    [name.Qualified, featureId.Value, nextId, attachmentName, resolved, (long)content.Length, keywords, content],
                    cancellationToken);
                return new FeatureAttachmentDescriptor(nextId, attachmentName, resolved, content.Length, keywords);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation && attempt + 1 < MaxInsertAttempts)
            {
                // Two writers read the same high-water mark; re-read it and try the next id.
            }
        }
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
        var identityKinds = description.IdColumns
            .Select(column => description.Schema[description.Schema.IndexOf(column)].Kind)
            .ToArray();
        var values = PostgisDiagnostics.ParseFeatureIdentity(identityKinds, featureId);
        await using var connection = await _store.OpenIngestConnectionAsync(cancellationToken);
        var rows = await PostgisDataStore.ReadRowsAsync(
            connection, PostgisQueries.FeatureExists(name, description.IdColumns), values, cancellationToken);
        if (rows.Count == 0)
        {
            throw SpatialException.Missing($"No feature with identity '{featureId}' exists in dataset '{name}'.");
        }
    }

    private void CheckQuota(string name, long size)
    {
        if (size > _maxBytesPerAttachment)
        {
            throw SpatialException.BadArguments(
                $"Attachment '{name}' is {size} bytes, over the {_maxBytesPerAttachment}-byte per-attachment quota.");
        }
    }

    private static void RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw SpatialException.BadArguments("An attachment name is required.");
        }
    }

    private static void RequireContent(byte[]? content)
    {
        if (content is null)
        {
            throw SpatialException.BadArguments("Attachment content is required.");
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

    private static FeatureAttachmentContent MapContent(IReadOnlyList<object?> row) =>
        new(MapDescriptor(row), (byte[])row[5]!);
}
