namespace Spatial.Core.Features;

/// <summary>
/// The contract face of a feature batch (ADR-0029): one schema plus the
/// features validated against it. Streams and canonical encoding bind this
/// interface where they only need the batch shape;
/// <see cref="FeatureBatch"/> is the immutable implementation.
/// </summary>
public interface IFeatureBatch
{
    /// <summary>The schema every contained feature is validated against.</summary>
    IFeatureSchema Schema { get; }

    /// <summary>The contained features.</summary>
    IReadOnlyList<Feature> Features { get; }

    /// <summary>Number of contained features.</summary>
    int Count { get; }
}
