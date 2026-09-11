namespace Spatial.Core.Features;

/// <summary>
/// An immutable batch of features sharing one exact schema, suitable for
/// streaming and interchange. Every member feature must carry the batch
/// schema (content-equal, not necessarily the same instance).
/// </summary>
public sealed class FeatureBatch : IFeatureBatch, IEquatable<FeatureBatch>
{
    private readonly Feature[] _features;

    public FeatureBatch(FeatureSchema schema, IReadOnlyList<Feature> features)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(features);

        Schema = schema;

        var values = features.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is null)
            {
                throw new ArgumentException($"Feature batch entry {i} is null.", nameof(features));
            }

            if (!values[i].Schema.Equals(schema))
            {
                throw new ArgumentException($"Feature batch entry {i}: feature schema does not match the batch schema.", nameof(features));
            }
        }

        _features = values;
    }

    public FeatureSchema Schema { get; }

    IFeatureSchema IFeatureBatch.Schema => Schema;

    /// <summary>The features, in batch order.</summary>
    public IReadOnlyList<Feature> Features => _features;

    public int Count => _features.Length;

    public Feature this[int index] => _features[index];

    public bool Equals(FeatureBatch? other) => other is not null && ContentEquals(other);

    public override bool Equals(object? obj) => obj is FeatureBatch other && ContentEquals(other);

    private bool ContentEquals(FeatureBatch other)
    {
        if (!Schema.Equals(other.Schema) || _features.Length != other._features.Length)
        {
            return false;
        }

        for (var i = 0; i < _features.Length; i++)
        {
            if (!_features[i].Equals(other._features[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Schema);
        foreach (var feature in _features)
        {
            hash.Add(feature);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"FeatureBatch ({Count} features, {Schema})";
}
