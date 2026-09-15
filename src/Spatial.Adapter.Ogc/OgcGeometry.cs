using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Core-typed geometry helpers for the OGC projections (ADR-0053 §3):
/// bbox parsing, dataset extents and envelope reprojection. Everything is
/// structural — reading envelopes and delegating transforms to
/// <see cref="ICoordinateTransforms"/> — so no spatial algorithm lives here.
/// </summary>
internal static class OgcGeometry
{
    /// <summary>Parses the four WMS/WFS bbox ordinates.</summary>
    public static Envelope ParseBbox(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            throw OgcServiceException.Invalid($"The 'bbox' parameter must be minx,miny,maxx,maxy, got '{text}'.");
        }

        var values = new double[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index])
                || !double.IsFinite(values[index]))
            {
                throw OgcServiceException.Invalid($"The 'bbox' parameter has a non-numeric ordinate '{parts[index]}'.");
            }
        }

        try
        {
            return new Envelope(values[0], values[1], values[2], values[3]);
        }
        catch (ArgumentException exception)
        {
            throw OgcServiceException.Invalid($"The 'bbox' parameter is invalid: {exception.Message}");
        }
    }

    /// <summary>Web Mercator's maximum latitude: the projection is undefined at the poles.</summary>
    public const double WebMercatorLatitudeLimit = 85.05112877980659;

    /// <summary>Reprojects an envelope through the transform service; the result's envelope covers the transformed corners.</summary>
    public static Envelope Transform(Envelope envelope, string source, string target, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        if (envelope.IsEmpty || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return envelope;
        }

        var polygon = GeometryFactory.CreatePolygon(
        [
            new Coordinate(envelope.MinX, envelope.MinY),
            new Coordinate(envelope.MaxX, envelope.MinY),
            new Coordinate(envelope.MaxX, envelope.MaxY),
            new Coordinate(envelope.MinX, envelope.MaxY),
            new Coordinate(envelope.MinX, envelope.MinY),
        ]);
        return transforms.Transform(polygon, source, target, cancellationToken).Envelope ?? envelope;
    }

    /// <summary>
    /// Projects a WGS84 envelope to EPSG:3857, clamping the latitude to Web
    /// Mercator's valid domain first: a global dataset whose extent reaches a
    /// pole is a valid WGS84 extent, but the projection is undefined there.
    /// </summary>
    public static Envelope ToWebMercator(
        Envelope wgs84, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
        Transform(ClampLatitude(wgs84, WebMercatorLatitudeLimit), "EPSG:4326", "EPSG:3857", transforms, cancellationToken);

    private static Envelope ClampLatitude(Envelope envelope, double limit) =>
        new(
            envelope.MinX,
            Math.Clamp(envelope.MinY, -limit, limit),
            envelope.MaxX,
            Math.Clamp(envelope.MaxY, -limit, limit));

    /// <summary>The union of every geometry envelope in a dataset, or <c>null</c> when it has no geometry.</summary>
    public static async Task<Envelope?> ExtentAsync(IFeatureStore store, string dataset, CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(dataset, cancellationToken);
        Envelope? extent = null;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            foreach (var attribute in feature.Attributes)
            {
                if (attribute.Kind == AttributeKind.Geometry
                    && !attribute.IsNull
                    && attribute.GeometryValue.Envelope is { } envelope)
                {
                    extent = Union(extent, envelope);
                }
            }
        }

        return extent;
    }

    private static Envelope Union(Envelope? current, Envelope next) =>
        current is not { } value
            ? next
            : new Envelope(
                Math.Min(value.MinX, next.MinX),
                Math.Min(value.MinY, next.MinY),
                Math.Max(value.MaxX, next.MaxX),
                Math.Max(value.MaxY, next.MaxY));
}
