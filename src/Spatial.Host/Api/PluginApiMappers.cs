using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Api;

/// <summary>
/// Plugin-surface mapping: a supervised worker package into the SDK's HTTP
/// plugin shape (plan §16 Phase 9, Epic F) — identity, lifecycle and the
/// manifest capabilities the accessibility/runtime-status screens show.
/// </summary>
internal static class PluginApiMappers
{
    public static PluginDto ToPluginDto(WorkerInstance worker)
    {
        var manifest = worker.Package.Manifest;
        return new PluginDto(
            worker.ProviderId.ToString(),
            manifest.DisplayName,
            manifest.Runtime,
            worker.State.ToString(),
            worker.RestartCount,
            worker.ProcessId,
            worker.StartedAt,
            worker.LastHealthyAt,
            worker.LastError,
            manifest.Capabilities
                .Select(capability => new PluginCapabilityDto(
                    capability.Id,
                    capability.Purpose,
                    capability.InputSchema,
                    capability.OutputSchema,
                    capability.Traits,
                    capability.Permissions,
                    capability.Errors
                        .Select(error => new ErrorVariantDto(error.Code, error.Description))
                        .ToArray()))
                .ToArray());
    }
}
