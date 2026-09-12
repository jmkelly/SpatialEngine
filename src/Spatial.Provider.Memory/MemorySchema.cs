using System.Globalization;
using Spatial.Core.Features;
using Spatial.PluginSdk;

namespace Spatial.Provider.Memory;

/// <summary>
/// Pure feature/schema mapping for the in-memory provider (ADR-0042): the
/// writable-schema check and the construction of a stored feature from a
/// caller's feature, assigning a generated identity when requested. Kept
/// separate from the store so the rules are unit-testable without the
/// catalog.
/// </summary>
internal static class MemorySchema
{
    /// <summary>The column name an auto-identity dataset assigns.</summary>
    public const string AutoIdentityColumn = "id";

    /// <summary>Rejects a batch field that is not a column of the dataset or has the wrong kind.</summary>
    public static void CheckWritable(MemoryDataset dataset, FeatureBatch batch)
    {
        foreach (var field in batch.Schema.Fields)
        {
            var index = dataset.Schema.IndexOf(field.Name);
            if (index < 0)
            {
                throw SpatialException.BadArguments(
                    $"The batch field '{field.Name}' is not a column of dataset '{dataset.Id}'.");
            }

            if (dataset.Schema[index].Kind != field.Kind)
            {
                throw SpatialException.BadArguments(
                    $"The batch field '{field.Name}' is {field.Kind} but the dataset column is {dataset.Schema[index].Kind}.");
            }
        }
    }

    /// <summary>
    /// Builds the feature as it is stored: one attribute per stored field, in
    /// schema order, taking values from the caller's feature by name. When
    /// <paramref name="assignedId"/> is given the auto-identity column is set
    /// to it (the input value is the placeholder); otherwise the identity is
    /// taken from the feature (or is unassigned, which is rejected).
    /// </summary>
    public static Feature BuildStored(MemoryDataset dataset, Feature feature, long? assignedId)
    {
        var values = new AttributeValue[dataset.Schema.Count];
        for (var i = 0; i < dataset.Schema.Count; i++)
        {
            values[i] = ValueFor(dataset, feature, i, assignedId);
        }

        return new Feature(IdentityFor(feature, assignedId), dataset.Schema, values);
    }

    /// <summary>One stored attribute: the generated identity, a source value, or a null for an absent nullable field.</summary>
    private static AttributeValue ValueFor(MemoryDataset dataset, Feature feature, int index, long? assignedId)
    {
        var field = dataset.Schema[index];
        if (field.Name == AutoIdentityColumn)
        {
            return IdentityValue(dataset, feature, assignedId);
        }

        var source = feature.Schema.IndexOf(field.Name);
        if (source >= 0)
        {
            return feature[source];
        }

        if (field.Nullable)
        {
            return AttributeValue.Null;
        }

        throw SpatialException.BadArguments($"The batch omits required field '{field.Name}' of dataset '{dataset.Id}'.");
    }

    /// <summary>The auto-identity value: the assigned id, the source value, or the feature's own numeric identity.</summary>
    private static AttributeValue IdentityValue(MemoryDataset dataset, Feature feature, long? assignedId)
    {
        if (assignedId is { } id)
        {
            return AttributeValue.FromInt64(id);
        }

        var identitySource = feature.Schema.IndexOf(AutoIdentityColumn);
        if (identitySource >= 0)
        {
            return feature[identitySource];
        }

        if (long.TryParse(feature.Id.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return AttributeValue.FromInt64(parsed);
        }

        throw SpatialException.BadArguments(
            $"The feature identity '{feature.Id}' cannot be used as the auto-identity value of dataset '{dataset.Id}'.");
    }

    /// <summary>The identity the stored feature reports: the assigned id, or the caller's identity.</summary>
    private static FeatureId IdentityFor(Feature feature, long? assignedId) =>
        assignedId is { } assigned
            ? new FeatureId(assigned.ToString(CultureInfo.InvariantCulture))
            : feature.Id;
}
