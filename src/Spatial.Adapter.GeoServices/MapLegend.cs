using System.IO.Compression;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Projects the MapServer <c>legend</c>, <c>queryDomains</c> and
/// <c>queryLegends</c> resources (S4, ADR-0055) from the persisted-style
/// projection. Every entry reuses <see cref="MapStyleProjection"/> — the
/// legend swatch of a renderer entry is a solid square of its symbol colour
/// — so the legend, the layer metadata and the domain queries always agree.
/// A layer with no projected renderer carries a single neutral swatch rather
/// than an empty legend.
/// </summary>
internal static class MapLegend
{
    private const int SwatchSize = 20;
    private const string SwatchMediaType = "image/png";

    /// <summary>Builds the <c>legend</c> response for every layer, in id order.</summary>
    public static EsriMapLegendResponse Legend(IReadOnlyList<MapLayerInfo> infos)
    {
        ArgumentNullException.ThrowIfNull(infos);
        return new EsriMapLegendResponse([.. infos.Select(Layer)]);
    }

    /// <summary>Builds the <c>queryLegends</c> response for the selected layers.</summary>
    public static EsriMapQueryLegendsResponse QueryLegends(IReadOnlyList<MapLayerInfo> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return new EsriMapQueryLegendsResponse([.. selected.Select(Layer)]);
    }

    /// <summary>Builds the <c>queryDomains</c> response for the selected layers.</summary>
    public static EsriMapQueryDomainsResponse QueryDomains(IReadOnlyList<MapLayerInfo> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var layers = new List<EsriLayerDomains>(selected.Count);
        foreach (var info in selected)
        {
            var drawing = MapStyleProjection.Project(info.Layer.Style, info.Dataset);
            var domains = MapStyleProjection.Domains(drawing, info.Dataset);
            layers.Add(new EsriLayerDomains(
                info.Layer.Id,
                domains ?? new Dictionary<string, EsriDomain>(StringComparer.Ordinal)));
        }

        return new EsriMapQueryDomainsResponse(layers);
    }

    internal static EsriLegendLayer Layer(MapLayerInfo info)
    {
        var drawing = MapStyleProjection.Project(info.Layer.Style, info.Dataset);
        if (drawing?.Renderer is not { } renderer)
        {
            return new EsriLegendLayer(
                info.Layer.Id, info.Layer.Name, "Feature Layer", 0, 0,
                [Swatch(string.Empty, NeutralSwatch(info), null)],
                [new EsriLegendGroup("0", string.Empty)]);
        }

        return renderer switch
        {
            { Type: "uniqueValue", UniqueValueInfos: { Count: > 0 } infos } =>
                Grouped(info, renderer.Field1 ?? string.Empty, infos.Select(entry =>
                    (entry.Label ?? entry.Value, entry.Symbol.Color, (object?)entry.Value))),
            { Type: "classBreaks", ClassBreakInfos: { Count: > 0 } breaks } =>
                Grouped(info, renderer.Field ?? string.Empty, breaks.Select(entry =>
                    (entry.Label ?? Format(entry.ClassMaxValue), entry.Symbol.Color, (object?)entry.ClassMaxValue))),
            { Symbol: { } symbol } =>
                Grouped(info, string.Empty, [(string.Empty, symbol.Color, (object?)null)]),
            _ => Grouped(info, string.Empty, [(string.Empty, NeutralSwatch(info), (object?)null)]),
        };
    }

    private static EsriLegendLayer Grouped(
        MapLayerInfo info, string heading, IEnumerable<(string Label, IReadOnlyList<int> Color, object? Value)> entries)
    {
        var swatches = entries
            .Select(entry => Swatch(entry.Label, entry.Color, entry.Value))
            .ToArray();
        return new EsriLegendLayer(
            info.Layer.Id, info.Layer.Name, "Feature Layer", 0, 0,
            swatches, [new EsriLegendGroup("0", heading)]);
    }

    private static EsriLegendEntry Swatch(string label, IReadOnlyList<int> color, object? value)
    {
        var imageData = Convert.ToBase64String(PngSwatch.Encode(color, SwatchSize, SwatchSize));
        return new EsriLegendEntry(
            label,
            LegendUrl(label, color),
            imageData,
            SwatchMediaType,
            SwatchSize,
            SwatchSize,
            "0",
            value is null ? null : [value]);
    }

    /// <summary>A stable swatch token from its label and colour (the G1 <c>url</c> is a content hash).</summary>
    private static string LegendUrl(string label, IReadOnlyList<int> color)
    {
        var hash = new HashCode();
        hash.Add(label, StringComparer.Ordinal);
        foreach (var channel in color)
        {
            hash.Add(channel);
        }

        return hash.ToHashCode().ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<int> NeutralSwatch(MapLayerInfo info) =>
        MapStyleProjection.Project(info.Layer.Style)?.Renderer?.Symbol?.Color ?? [128, 128, 128, 255];

    private static string Format(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Encodes a solid-colour swatch as PNG bytes with the framework only (no
/// renderer dependency crosses into the adapter — ADR-0005). 8-bit RGBA with
/// filter-zero scanlines; the CRC is the standard IEEE checksum.
/// </summary>
internal static class PngSwatch
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(IReadOnlyList<int> rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A swatch needs a positive size.");
        }

        var color = new byte[] { Clamp(rgba, 0), Clamp(rgba, 1), Clamp(rgba, 2), Clamp(rgba, 3) };
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(output, "IHDR", Ihdr(width, height));
        WriteChunk(output, "IDAT", Deflate(RawScanlines(color, width, height)));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte[] Ihdr(int width, int height)
    {
        var chunk = new byte[13];
        WriteInt32(chunk, 0, width);
        WriteInt32(chunk, 4, height);
        chunk[8] = 8;
        chunk[9] = 6;
        return chunk;
    }

    private static byte[] RawScanlines(byte[] color, int width, int height)
    {
        var row = new byte[1 + width * 4];
        for (var x = 0; x < width; x++)
        {
            Buffer.BlockCopy(color, 0, row, 1 + x * 4, 4);
        }

        var raw = new byte[row.Length * height];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(row, 0, raw, y * row.Length, row.Length);
        }

        return raw;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var header = new byte[8];
        WriteInt32(header, 0, data.Length);
        header[4] = (byte)type[0];
        header[5] = (byte)type[1];
        header[6] = (byte)type[2];
        header[7] = (byte)type[3];
        output.Write(header, 0, header.Length);
        output.Write(data, 0, data.Length);
        var crc = new byte[4];
        WriteInt32(crc, 0, unchecked((int)Crc(header.AsSpan(4), data)));
        output.Write(crc, 0, crc.Length);
    }

    private static uint Crc(ReadOnlySpan<byte> type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var value = 0; value < 256; value++)
        {
            var entry = (uint)value;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) == 1 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;
            }

            table[value] = entry;
        }

        return table;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static byte Clamp(IReadOnlyList<int> rgba, int index) =>
        rgba.Count > index ? (byte)Math.Clamp(rgba[index], 0, 255) : (byte)(index == 3 ? 255 : 0);
}
