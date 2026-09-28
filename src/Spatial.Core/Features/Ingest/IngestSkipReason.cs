namespace Spatial.Core.Features.Ingest;

/// <summary>
/// Why one source record did not become a feature (ADR-0041 §4). The codes are
/// dotted and stable like the <c>SpatialException</c> codes, because a decode
/// report is a diagnostic surface a caller reads and an operator greps.
/// </summary>
public enum IngestSkipReason
{
    /// <summary>The record was not parseable as the declared format.</summary>
    RecordMalformed,

    /// <summary>The record parsed but its geometry did not decode.</summary>
    GeometryInvalid,

    /// <summary>A value could not be represented in the field's inferred kind.</summary>
    AttributeInvalid,

    /// <summary>
    /// The record carried an attribute the inferred schema has no field for,
    /// which a streaming decode can only discover after the schema is fixed.
    /// </summary>
    FieldNotInferred,

    /// <summary>
    /// The record's geometry declares a CRS that conflicts with the decode's
    /// source CRS; a per-feature CRS override is not honoured.
    /// </summary>
    CrsConflict,
}
