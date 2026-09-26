using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The <c>outSR</c> reprojection guard (spec §9.1.4): a feature is reprojected
/// only when a target CRS and a layer CRS are both known and differ, so a
/// missing <c>outSR</c>, a layer without a CRS, or an <c>outSR</c> equal to the
/// layer CRS all return the original geometry untouched.
/// </summary>
public sealed class FeatureProjectionTransformTests
{
    private static readonly CoordinateReference Wgs84 = CoordinateReference.Epsg(4326);

    private static readonly CoordinateReference WebMercator = CoordinateReference.Epsg(3857);

    [Fact]
    public void A_different_target_reprojects_through_the_transform_service()
    {
        var point = GeometryFactory.CreatePoint(13.4, 52.5, Wgs84);

        var transformed = FeatureProjection.TransformGeometry(
            point, Wgs84, WebMercator, RecordingTransforms.Instance, CancellationToken.None);

        Assert.NotSame(point, transformed);
        Assert.Equal(("EPSG:4326", "EPSG:3857"), RecordingTransforms.Instance.LastCall);
    }

    [Fact]
    public void The_same_crs_returns_the_geometry_without_reprojecting()
    {
        var point = GeometryFactory.CreatePoint(13.4, 52.5, Wgs84);

        var transformed = FeatureProjection.TransformGeometry(
            point, Wgs84, Wgs84, RecordingTransforms.Instance, CancellationToken.None);

        Assert.Same(point, transformed);
        Assert.Null(RecordingTransforms.Instance.LastCall);
    }

    [Fact]
    public void A_missing_source_crs_returns_the_geometry_untouched()
    {
        var point = GeometryFactory.CreatePoint(13.4, 52.5, Wgs84);

        var transformed = FeatureProjection.TransformGeometry(
            point, null, WebMercator, RecordingTransforms.Instance, CancellationToken.None);

        Assert.Same(point, transformed);
        Assert.Null(RecordingTransforms.Instance.LastCall);
    }

    [Fact]
    public void A_missing_target_crs_returns_the_geometry_untouched()
    {
        var point = GeometryFactory.CreatePoint(13.4, 52.5, Wgs84);

        var transformed = FeatureProjection.TransformGeometry(
            point, Wgs84, null, RecordingTransforms.Instance, CancellationToken.None);

        Assert.Same(point, transformed);
        Assert.Null(RecordingTransforms.Instance.LastCall);
    }

    private sealed class RecordingTransforms : ICoordinateTransforms
    {
        internal static RecordingTransforms Instance { get; } = new();

        internal (string? Source, string Target)? LastCall { get; private set; }

        public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
        {
            LastCall = (source, target);
            return GeometryFactory.CreatePoint(geometry.Envelope!.Value.MaxX, geometry.Envelope.Value.MaxY, Wgs84);
        }
    }
}
