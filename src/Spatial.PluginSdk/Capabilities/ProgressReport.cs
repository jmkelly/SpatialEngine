namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// One progress observation for a long-running invocation. The fraction is
/// the completed share of the work in [0, 1] (or null when progress cannot be
/// quantified); the message is optional human-readable context. Timestamps
/// are recorded at creation.
/// </summary>
public sealed record ProgressReport(double? Fraction, string? Message, DateTimeOffset Timestamp)
{
    /// <summary>
    /// Creates a quantified progress report, validating that
    /// <paramref name="fraction"/> lies in [0, 1]. Use a null fraction when
    /// work cannot be quantified (for example "buffering…").
    /// </summary>
    public static ProgressReport Create(double fraction, string? message)
    {
        if (fraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fraction), fraction, "A progress fraction must lie in [0, 1].");
        }

        return new ProgressReport(fraction, message, DateTimeOffset.UtcNow);
    }

    /// <summary>An unquantified progress milestone.</summary>
    public static ProgressReport Milestone(string message) =>
        new(null, message, DateTimeOffset.UtcNow);
}