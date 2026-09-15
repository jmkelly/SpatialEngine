using System.Security.Cryptography;
using System.Text;
using Spatial.Esri.Codec;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Image Service legend response (spec §8.0.1 legend): band-id parsing
/// and legend-entry building. Split out of <see cref="ImageService"/> so
/// the service facade keeps only orchestration-shaped responses and the
/// legend fan-out lives with the code that uses it (ADR-0040).
/// </summary>
internal static class ImageLegendBuilder
{
    /// <summary>
    /// Builds the Legend resource (S3 legend-image-service/): one entry per
    /// band labelled <c>Band_N</c> (the Esri <c>bandNames</c> convention),
    /// each carrying the 20x20 dataset render as base64 <c>imageData</c>.
    /// The render is what <c>exportImage</c> serves, so the swatch is honest
    /// without inventing a renderer the engine does not have.
    /// </summary>
    internal static EsriImageLegend Legend(
        RasterDatasetDescription description, byte[] swatch, int width, int height, IReadOnlyList<long> bandIds) =>
        new(
        [
            new EsriImageLegendLayer(
                0,
                description.Name,
                "Raster Layer",
                0,
                0,
                LegendType(bandIds.Count),
                [.. bandIds.Select(id => new EsriImageLegendEntry(
                    $"Band_{id + 1}",
                    LegendUrl(description.Dataset, id),
                    Convert.ToBase64String(swatch),
                    "image/png",
                    height,
                    width))]),
        ]);

    /// <summary>
    /// Parses the optional legend <c>bandIds</c> (0-based): all bands by
    /// default, otherwise the named bands in order. Unknown ids are invalid
    /// arguments rather than silently dropped entries.
    /// </summary>
    internal static IReadOnlyList<long> ParseLegendBandIds(string? value, int bandCount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [.. Enumerable.Range(0, bandCount).Select(id => (long)id)];
        }

        var ids = EsriValueParser.ParseInt64s(value, "bandIds");
        foreach (var id in ids)
        {
            if (id < 0 || id >= bandCount)
            {
                throw GeoServicesErrors.Invalid(
                    $"Band id {id} is out of range: the service has {bandCount} band(s) and bandIds are 0-based.");
            }
        }

        return ids;
    }

    /// <summary>The renderer flavour the legend describes: raw multi-band renders read as RGB composites.</summary>
    private static string LegendType(int selected) => selected is 3 or 4 ? "RGB Composite" : "Stretched";

    /// <summary>A stable opaque swatch id: 32 hex chars like the Esri reference.</summary>
    private static string LegendUrl(string dataset, long bandId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{dataset}:{bandId}")).AsSpan(0, 16)).ToLowerInvariant();
}
