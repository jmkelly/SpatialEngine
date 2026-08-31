using Npgsql;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Streams;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The pure catalogue/description emission helpers: mint nothing, own no
/// state — they write summary/description JSON items to an already-created
/// channel and fail the channel with a redacted stream error. The channel
/// minting lives in the callers; failures that belong to the invocation (not
/// the stream) propagate to the guarded facade.
/// </summary>
internal static class PostgisMetadataEmitter
{
    public static async Task EmitCatalogueAsync(
        PostgisConnectionConfiguration configuration,
        Lazy<PostgisDataStore> store,
        CapabilityId capability,
        StreamChannel channel,
        string? pattern,
        CancellationToken token)
    {
        try
        {
            await using var connection = await store.Value.OpenConnectionAsync(token);
            await using var reader = await PostgisDataStore.ExecuteReaderAsync(connection, PostgisQueries.Catalogue(pattern), CatalogueParameters(pattern), token);
            await EmitCatalogueLoopAsync(channel, reader, token);
            channel.Writer.Complete();
        }
        catch (Exception exception)
        {
            channel.Writer.Complete(PostgisDiagnostics.StreamFailure(capability, exception, configuration));
        }
    }

    public static async Task EmitDescriptionAsync(
        PostgisConnectionConfiguration configuration,
        CapabilityInvocation invocation,
        StreamChannel channel,
        DatasetDescription description,
        CancellationToken token)
    {
        try
        {
            await channel.Writer.WriteAsync(DatasetMetadataJson.WriteDescription(description), token);
            channel.Writer.Complete();
        }
        catch (Exception exception)
        {
            channel.Writer.Complete(PostgisDiagnostics.StreamFailure(invocation.Capability, exception, configuration));
        }
    }

    /// <summary>The positional parameters of a (filtered) catalogue query: the pattern, or none.</summary>
    private static object?[] CatalogueParameters(string? pattern) =>
        pattern is null ? [] : new object?[] { pattern };

    private static async Task EmitCatalogueLoopAsync(StreamChannel channel, NpgsqlDataReader reader, CancellationToken token)
    {
        while (await reader.ReadAsync(token))
        {
            var row = PostgisDataStore.ReadRow(reader, reader.FieldCount);
            await channel.Writer.WriteAsync(DatasetMetadataJson.WriteSummary(PostgisSchemaDiscovery.SummaryFromRow(row)), token);
        }
    }
}
