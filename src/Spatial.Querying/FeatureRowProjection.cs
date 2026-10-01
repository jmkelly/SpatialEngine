using Spatial.Core.Features;

namespace Spatial.Querying;

/// <summary>
/// How a store's row mapper turns one mapped row into the feature a read
/// answers with: the read schema carries whatever the row mapper needed and the
/// result schema is what the caller asked for, so a read that appended the
/// dataset's identity columns has to drop them again — and a read that appended
/// nothing has to drop nothing.
///
/// <para>
/// That decision is made once, here, rather than once per row in each store:
/// both SQL stores map a row, then project it, and a projection that dropped
/// nothing used to build a <em>second</em> feature for every row anyway. On a
/// whole read — the answer a keyless layer's plan gets, since no pushed
/// restriction is admissible there (ADR-0097 §1, ADR-0184 §2) — that is a
/// second materialisation of the whole table, and it made a capped read cost
/// more than the scan it had to do anyway (SpatialEngine-yup).
///
/// <para>
/// It is a value with no state beyond its two schemas, so a store builds it
/// once per read and applies it per row; the applying half allocates nothing
/// when there is nothing to drop.
/// </para>
/// </summary>
public sealed class FeatureRowProjection
{
    private readonly int[]? _indexes;

    /// <summary>
    /// The projection from what was read to what is returned. <paramref name="result"/>
    /// must name fields of <paramref name="read"/>; a field the read does not
    /// carry is refused here, once, rather than per row.
    /// </summary>
    public FeatureRowProjection(FeatureSchema read, FeatureSchema result)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(result);

        Read = read;
        Result = result;

        if (read.Equals(result))
        {
            // Same fields, same order, same kinds: there is nothing to drop,
            // and the row's own feature is the feature the read answers with.
            return;
        }

        var indexes = new int[result.Count];
        for (var i = 0; i < indexes.Length; i++)
        {
            if (read.IndexOf(result[i].Name) is var index && index < 0)
            {
                throw new ArgumentException(
                    $"The read schema has no field named '{result[i].Name}', which the result schema declares.",
                    nameof(result));
            }

            indexes[i] = index;
        }

        _indexes = indexes;
    }

    /// <summary>The schema the row was mapped against: every column the read needed.</summary>
    public FeatureSchema Read { get; }

    /// <summary>The schema the page is answered with: the plan's projection.</summary>
    public FeatureSchema Result { get; }

    /// <summary>
    /// The feature a mapped row is answered with: the row's own feature when
    /// the read dropped nothing, and a feature over the result schema's fields
    /// when it did. A feature is immutable, so sharing it is sharing an
    /// answer, not a mutable buffer.
    /// </summary>
    public Feature Apply(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        if (_indexes is null)
        {
            return feature;
        }

        var values = new AttributeValue[_indexes.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = feature[_indexes[i]];
        }

        return new Feature(feature.Id, Result, values);
    }
}
