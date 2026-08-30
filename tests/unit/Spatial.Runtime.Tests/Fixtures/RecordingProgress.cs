using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests.Fixtures;

/// <summary>
/// A deterministic <see cref="IProgress{T}"/> for tests: records reports
/// synchronously on the reporting thread. <c>Progress&lt;T&gt;</c> with no
/// synchronisation context posts its handler asynchronously, which makes
/// count/order assertions flaky under load — the tests are about the
/// *provider's* emission order and fractions, not the BCL delivery.
/// </summary>
public sealed class RecordingProgress : IProgress<ProgressReport>
{
    private readonly List<ProgressReport> _reports;

    public RecordingProgress(List<ProgressReport> reports)
    {
        _reports = reports;
    }

    public void Report(ProgressReport value) => _reports.Add(value);
}
