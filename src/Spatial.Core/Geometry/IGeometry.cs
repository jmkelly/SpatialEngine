namespace Spatial.Core.Geometry;

/// <summary>
/// An immutable simple-feature geometry value. The core stays structural:
/// inspection, enumeration, traversal, envelopes and canonical round trips
/// live here; algorithms live in plugins (ADR-0001/0002).
/// </summary>
public interface IGeometry
{
    /// <summary>Simple-feature type of this geometry.</summary>
    GeometryType Type { get; }

    /// <summary>Which ordinates the geometry carries (the merged layout for collections).</summary>
    CoordinateLayout Layout { get; }

    /// <summary>CRS identity, or <c>null</c> when unspecified (ADR-0009).</summary>
    CoordinateReference? CoordinateReference { get; }

    /// <summary>Whether the geometry has no spatial content.</summary>
    bool IsEmpty { get; }

    /// <summary>Total number of coordinates across all parts.</summary>
    int CoordinateCount { get; }

    /// <summary>
    /// The bounding envelope, or <c>null</c> when the geometry has no
    /// coordinates. Computed from minima and maxima — structural behaviour.
    /// </summary>
    Envelope? Envelope { get; }
}
