using Spatial.PluginSdk.Capabilities;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Geometry;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// Maps a store-side exception to a redacted capability error under one roof
/// (ADR-0028): known domain exceptions become invalid.arguments naming the
/// rejected value; everything else becomes a redacted provider failure. Pure
/// and shared by every guarded store operation.
/// </summary>
internal static class PostgisFailureMapper
{
    public static CapabilityError Map(
        CapabilityInvocation invocation,
        Exception exception,
        PostgisConnectionConfiguration configuration)
    {
        if (exception is PostgisUnknownDatasetException unknown)
        {
            return PostgisDiagnostics.InvalidArgument(invocation.Capability, unknown.Message);
        }

        if (exception is PostgisCrsMismatchException mismatch)
        {
            return PostgisDiagnostics.InvalidArgument(invocation.Capability, mismatch.Message);
        }

        if (exception is PostgisInactiveTransactionException inactive)
        {
            return PostgisDiagnostics.InvalidArgument(invocation.Capability, inactive.Message);
        }

        return PostgisDiagnostics.ProviderFailure(configuration, invocation.Capability, exception);
    }
}
