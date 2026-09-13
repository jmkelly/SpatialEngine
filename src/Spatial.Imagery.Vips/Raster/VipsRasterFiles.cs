using NetVips;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>Opens a configured raster file, mapping a missing file to a typed <c>not.found</c>.</summary>
internal static class VipsRasterFiles
{
    public static Image Open(string path)
    {
        if (!File.Exists(path))
        {
            throw SpatialException.Missing($"Raster file '{path}' does not exist.");
        }

        return Image.NewFromFile(path);
    }
}
