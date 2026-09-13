using System.Globalization;
using NetVips;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Resolves, validates and reads the raw files behind a configured raster
/// dataset (ADR-0051). The contract exposes an opaque file id, never a path:
/// the provider builds the id from the catalog identity and the file name,
/// and validates it against the file it actually owns before reading, so a
/// caller can never name an arbitrary path. Paths stay inside this assembly.
/// </summary>
internal static class VipsRasterFiles
{
    private const string DatasetPrefix = "dataset";

    /// <summary>Opens a configured raster file, mapping a missing file to a typed <c>not.found</c>.</summary>
    public static Image Open(string path)
    {
        if (!File.Exists(path))
        {
            throw SpatialException.Missing($"Raster file '{path}' does not exist.");
        }

        return Image.NewFromFile(path);
    }

    /// <summary>Lists the raw files backing the dataset or one catalog item.</summary>
    public static IReadOnlyList<RasterFile> List(RasterDatasetDescriptor descriptor, long? rasterId)
    {
        if (rasterId is { } id)
        {
            return [Describe(ResolvePath(descriptor, id), id)];
        }

        if (VipsRasterReader.HasCatalog(descriptor))
        {
            return [.. descriptor.Items!.Select(item => Describe(item.Path, item.ObjectId))];
        }

        return [Describe(descriptor.Path, null)];
    }

    /// <summary>Reads the file named by an opaque id obtained from <see cref="List"/>.</summary>
    public static RasterFileContent Read(RasterDatasetDescriptor descriptor, string fileId)
    {
        var (id, name) = ParseFileId(fileId);
        var path = ResolvePath(descriptor, id);
        if (!string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))
        {
            throw SpatialException.Missing($"Raster file '{fileId}' does not exist in '{descriptor.Name}'.");
        }

        if (!File.Exists(path))
        {
            throw SpatialException.Missing($"Raster file '{name}' does not exist in '{descriptor.Name}'.");
        }

        var content = File.ReadAllBytes(path);
        return new RasterFileContent(content, MediaType(path), name, content.LongLength);
    }

    /// <summary>The media type of a raw raster file, from its extension.</summary>
    public static string MediaType(string path) =>
        MediaTypes.GetValueOrDefault(Path.GetExtension(path).ToLowerInvariant(), DefaultMediaType);

    private const string DefaultMediaType = "application/octet-stream";

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.Ordinal)
    {
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".jp2"] = "image/jp2",
        [".j2k"] = "image/jp2",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".img"] = DefaultMediaType,
    };

    private static RasterFile Describe(string path, long? rasterId) =>
        new(FileId(rasterId, path), Path.GetFileName(path), MediaType(path), new FileInfo(path).Length);

    private static string FileId(long? rasterId, string path) =>
        $"{(rasterId?.ToString(CultureInfo.InvariantCulture) ?? DatasetPrefix)}~{Path.GetFileName(path)}";

    private static (long? RasterId, string Name) ParseFileId(string fileId)
    {
        var separator = fileId.IndexOf('~', StringComparison.Ordinal);
        if (separator <= 0 || separator == fileId.Length - 1)
        {
            throw SpatialException.BadArguments($"Raster file id '{fileId}' is not valid.");
        }

        var prefix = fileId[..separator];
        var name = fileId[(separator + 1)..];
        if (prefix == DatasetPrefix)
        {
            return (null, name);
        }

        return long.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rasterId)
            ? (rasterId, name)
            : throw SpatialException.BadArguments($"Raster file id '{fileId}' is not valid.");
    }

    private static string ResolvePath(RasterDatasetDescriptor descriptor, long? rasterId)
    {
        if (rasterId is { } id)
        {
            var item = descriptor.Items?.FirstOrDefault(candidate => candidate.ObjectId == id)
                ?? throw SpatialException.Missing($"Raster catalog item {id} does not exist in '{descriptor.Name}'.");
            if (string.IsNullOrWhiteSpace(item.Path))
            {
                throw SpatialException.Missing($"Raster catalog item {id} of '{descriptor.Name}' has no file.");
            }

            return item.Path;
        }

        if (VipsRasterReader.HasCatalog(descriptor))
        {
            throw SpatialException.BadArguments(
                $"Raster dataset '{descriptor.Name}' is a catalog; name a catalog item to read its file.");
        }

        return descriptor.Path;
    }
}
