using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Stores.Memory;

/// <summary>
/// The in-memory feature-attachment face (T-060, ADR-0065), split from
/// <see cref="MemoryStore"/> exactly like <see cref="MemoryEditor"/> so each
/// type keeps one cohesive responsibility. Attachments are keyed by dataset
/// and feature identity with per-feature integer ids starting at one; bytes
/// are copied on write and on read so callers only ever hold provider-owned
/// copies. State is <em>non-durable</em> like the rest of the memory
/// provider: a process restart loses every attachment. Writes are bound by
/// <see cref="DefaultMaxBytesPerAttachment"/> unless a smaller cap is
/// supplied (tests); the PostGIS sidecar-table persistence lands separately.
/// </summary>
public sealed class MemoryAttachments : IFeatureAttachmentStore
{
    /// <summary>The default per-attachment byte cap (10 MiB, ADR-0065 §3).</summary>
    public const long DefaultMaxBytesPerAttachment = 10_485_760;

    private const string DefaultContentType = "application/octet-stream";

    private readonly MemoryStore _store;
    private readonly long _maxBytesPerAttachment;
    private readonly Dictionary<AttachmentKey, List<StoredAttachment>> _attachments = new();
    private readonly Dictionary<AttachmentKey, long> _nextIds = new();

    public MemoryAttachments(MemoryStore store, long maxBytesPerAttachment = DefaultMaxBytesPerAttachment)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerAttachment);
        _store = store;
        _maxBytesPerAttachment = maxBytesPerAttachment;
    }

    /// <inheritdoc cref="IFeatureAttachmentStore.ListAsync"/>
    public Task<IReadOnlyList<FeatureAttachmentDescriptor>> ListAsync(
        string dataset, FeatureId featureId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.WithLock<IReadOnlyList<FeatureAttachmentDescriptor>>(() =>
        {
            RequireFeature(dataset, featureId);
            return For(dataset, featureId).Select(stored => stored.Descriptor).ToArray();
        }));
    }

    /// <inheritdoc cref="IFeatureAttachmentStore.AddAsync"/>
    public Task<FeatureAttachmentDescriptor> AddAsync(
        string dataset, FeatureId featureId, string name, string contentType, byte[] content,
        string? keywords = null, CancellationToken cancellationToken = default)
    {
        RequireName(name);
        RequireContent(content);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.WithLock(() =>
        {
            RequireFeature(dataset, featureId);
            var key = new AttachmentKey(dataset, featureId);
            var id = NextId(key);
            var stored = new StoredAttachment(
                new FeatureAttachmentDescriptor(id, name, OrDefaultContentType(contentType), content.Length, keywords),
                Copy(content));
            CheckQuota(stored.Descriptor);
            _attachments[key].Add(stored);
            return stored.Descriptor;
        }));
    }

    /// <inheritdoc cref="IFeatureAttachmentStore.GetAsync"/>
    public Task<FeatureAttachmentContent> GetAsync(
        string dataset, FeatureId featureId, long attachmentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.WithLock(() =>
        {
            RequireFeature(dataset, featureId);
            var stored = Find(dataset, featureId, attachmentId);
            return new FeatureAttachmentContent(stored.Descriptor, Copy(stored.Content));
        }));
    }

    /// <inheritdoc cref="IFeatureAttachmentStore.UpdateAsync"/>
    public Task<FeatureAttachmentDescriptor> UpdateAsync(
        string dataset, FeatureId featureId, long attachmentId, string name, string contentType, byte[] content,
        string? keywords = null, CancellationToken cancellationToken = default)
    {
        RequireName(name);
        RequireContent(content);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.WithLock(() =>
        {
            RequireFeature(dataset, featureId);
            var stored = Find(dataset, featureId, attachmentId);
            var descriptor = new FeatureAttachmentDescriptor(
                stored.Descriptor.Id, name, OrDefaultContentType(contentType), content.Length, keywords);
            CheckQuota(descriptor);
            stored.Descriptor = descriptor;
            stored.Content = Copy(content);
            return descriptor;
        }));
    }

    /// <inheritdoc cref="IFeatureAttachmentStore.DeleteAsync"/>
    public Task<IReadOnlyList<FeatureAttachmentOutcome>> DeleteAsync(
        string dataset, FeatureId featureId, IReadOnlyList<long> attachmentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.WithLock<IReadOnlyList<FeatureAttachmentOutcome>>(() =>
        {
            RequireFeature(dataset, featureId);
            var outcomes = new List<FeatureAttachmentOutcome>(attachmentIds.Count);
            foreach (var attachmentId in attachmentIds)
            {
                var removed = For(dataset, featureId).RemoveAll(stored => stored.Descriptor.Id == attachmentId);
                outcomes.Add(removed > 0
                    ? FeatureAttachmentOutcome.Success(attachmentId)
                    : FeatureAttachmentOutcome.Failure(
                        attachmentId, SpatialException.NotFound, $"No attachment with identity '{attachmentId}' exists."));
            }

            return outcomes;
        }));
    }

    private List<StoredAttachment> For(string dataset, FeatureId featureId)
    {
        var key = new AttachmentKey(dataset, featureId);
        if (!_attachments.TryGetValue(key, out var list))
        {
            list = [];
            _attachments[key] = list;
        }

        return list;
    }

    private long NextId(AttachmentKey key)
    {
        // Start the per-feature sequence on first use so an empty feature
        // still has a list to append to.
        _ = For(key.Dataset, key.FeatureId);
        var id = _nextIds.GetValueOrDefault(key, 1);
        _nextIds[key] = id + 1;
        return id;
    }

    private StoredAttachment Find(string dataset, FeatureId featureId, long attachmentId) =>
        For(dataset, featureId).FirstOrDefault(stored => stored.Descriptor.Id == attachmentId)
        ?? throw SpatialException.Missing($"No attachment with identity '{attachmentId}' exists.");

    private void RequireFeature(string dataset, FeatureId featureId)
    {
        var found = _store.Catalog.Find(dataset);
        if (!found.Features.Any(feature => feature.Id.Equals(featureId)))
        {
            throw SpatialException.Missing($"No feature with identity '{featureId}' exists in dataset '{found.Id}'.");
        }
    }

    private void CheckQuota(FeatureAttachmentDescriptor descriptor)
    {
        if (descriptor.Size > _maxBytesPerAttachment)
        {
            throw SpatialException.BadArguments(
                $"Attachment '{descriptor.Name}' is {descriptor.Size} bytes, over the {_maxBytesPerAttachment}-byte per-attachment quota.");
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

    private static byte[] Copy(byte[] content)
    {
        var copy = new byte[content.Length];
        Buffer.BlockCopy(content, 0, copy, 0, content.Length);
        return copy;
    }

    private sealed record AttachmentKey(string Dataset, FeatureId FeatureId);

    private sealed class StoredAttachment(FeatureAttachmentDescriptor descriptor, byte[] content)
    {
        public FeatureAttachmentDescriptor Descriptor { get; set; } = descriptor;

        public byte[] Content { get; set; } = content;
    }
}
