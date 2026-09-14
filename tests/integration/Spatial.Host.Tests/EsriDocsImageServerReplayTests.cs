using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NetVips;

namespace Spatial.Host.Tests;

/// <summary>
/// Slice D part 1 (T-071): ImageServer replay over a runtime
/// <c>wsiearth</c> image service published on an 8x6 U8 gradient raster
/// (EPSG:4326). The default test host serves no ImageServer, so this class
/// carries its own factory (admin token, isolated publications file and an
/// inline raster source) and publishes the service once per test; the JSON
/// cases then replay their stored query strings verbatim with the shared
/// semantic diff (<see cref="EsriDocsReplayTests.Compare"/>). No
/// ImageServer behaviour was changed for this slice: the wire shape
/// (service envelope, export href, identify value/location) is Esri's, the
/// raster description and pixels are ours, and honest deltas live in
/// <c>knownDeltas</c>.
/// </summary>
/// <remarks>
/// Images are never byte-compared: <c>exportImage?f=image</c> gets a
/// structural PNG gate (media type, raster headers, IHDR dimensions,
/// non-trivial body) plus an RMSE determinism check between two identical
/// exports (RMSE must be 0 — the same render is pixel-identical to
/// itself). Renderers are never pixel-equal across engines, so there is no
/// Esri reference image to hash — the fixtures say so in
/// <c>knownDeltas</c> and <c>policy.json</c> (renders-are-engine-native).
/// </remarks>
public sealed class EsriDocsImageServerReplayTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string Service = "wsiearth";
    private const string Dataset = "raster.wsiearth";
    private const int Width = 8;
    private const int Height = 6;

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "esri-docs-fixtures", "imageserver");

    private static readonly JsonElement Manifest =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "manifest.json"))).RootElement;

    private static readonly double Tolerance = Manifest.GetProperty("tolerance").GetDouble();

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-esri-image-").FullName;
    private readonly WebApplicationFactory<Program> _factory;
    private HttpClient? _client;

    public EsriDocsImageServerReplayTests()
    {
        WriteRaster(Path.Combine(_directory, "wsiearth.tif"));
        _factory = new ImageReplayFactory(_directory, Token);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    public static IEnumerable<object[]> JsonCaseNames() =>
        Manifest.GetProperty("cases").EnumerateArray()
            .Where(entry => !entry.TryGetProperty("binary", out _))
            .Select(entry => new object[] { entry.GetProperty("name").GetString()! });

    public static IEnumerable<object[]> BinaryCaseNames() =>
        Manifest.GetProperty("cases").EnumerateArray()
            .Where(entry => entry.TryGetProperty("binary", out _))
            .Select(entry => new object[] { entry.GetProperty("name").GetString()! });

    /// <summary>Replays the fixture's verbatim query string and diffs the answer.</summary>
    [Theory]
    [MemberData(nameof(JsonCaseNames))]
    public async Task Image_docs_request_replays_against_our_host(string name)
    {
        var client = await ImageServiceAsync();
        var fixture = ReadFixture(name);
        var request = fixture.GetProperty("request");
        var url = $"{request.GetProperty("path").GetString()}?{request.GetProperty("query").GetString()}";

        var response = await client.GetAsync(url);
        Assert.Equal(fixture.GetProperty("expectStatus").GetInt32(), (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var deltas = new List<string>();
        EsriDocsReplayTests.Compare(fixture.GetProperty("expect"), body.RootElement, "$", deltas, Tolerance);
        Assert.True(
            deltas.Count == 0,
            $"Semantic diff for '{name}' ({deltas.Count} deltas):\n{string.Join("\n", deltas.Take(10))}");
    }

    /// <summary>
    /// The export image gate: a structural PNG check plus RMSE determinism,
    /// never byte equality against a foreign render. The media type, raster
    /// dimension headers, PNG signature, IHDR size and a non-trivial body
    /// come from the fixture's <c>expect</c>; determinism fetches the same
    /// export twice and requires RMSE 0 over decoded pixels.
    /// </summary>
    [Theory]
    [MemberData(nameof(BinaryCaseNames))]
    public async Task Image_export_image_is_a_structural_png_with_zero_rmse_redundancy(string name)
    {
        var client = await ImageServiceAsync();
        var fixture = ReadFixture(name);
        var request = fixture.GetProperty("request");
        var url = $"{request.GetProperty("path").GetString()}?{request.GetProperty("query").GetString()}";
        var expect = fixture.GetProperty("expect");

        var response = await client.GetAsync(url);
        Assert.Equal(fixture.GetProperty("expectStatus").GetInt32(), (int)response.StatusCode);
        Assert.Equal(expect.GetProperty("mediaType").GetString(), response.Content.Headers.ContentType?.MediaType);
        var width = expect.GetProperty("width").GetInt32();
        var height = expect.GetProperty("height").GetInt32();
        Assert.Equal(width.ToString(CultureInfo.InvariantCulture), response.Headers.GetValues("X-Raster-Width").Single());
        Assert.Equal(height.ToString(CultureInfo.InvariantCulture), response.Headers.GetValues("X-Raster-Height").Single());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(
            bytes.Length >= expect.GetProperty("minBytes").GetInt32(),
            $"Export image for '{name}' is suspiciously small ({bytes.Length} bytes).");
        AssertPng(bytes, width, height);

        var repeat = await (await client.GetAsync(url)).Content.ReadAsByteArrayAsync();
        var rmse = Rmse(bytes, repeat, width, height);
        Assert.Equal(0, rmse);
        Assert.True(Range(bytes, width, height) > 0, "Export image is blank; the replay raster must render non-trivial pixels.");
    }

    /// <summary>The corpus loop for slice D part 1: root/export/identify each have a case.</summary>
    [Fact]
    public void The_imageserver_manifest_covers_the_slice()
    {
        string[] required = ["root", "export", "identify"];
        var cases = Manifest.GetProperty("cases").EnumerateArray().ToArray();
        foreach (var facet in required)
        {
            Assert.Contains(cases, entry =>
                string.Equals(entry.GetProperty("covers").GetString(), facet, StringComparison.Ordinal));
        }

        Assert.Contains(cases, entry => entry.TryGetProperty("binary", out _));

        foreach (var entry in cases)
        {
            var fixture = ReadFixture(entry.GetProperty("name").GetString()!);
            Assert.False(string.IsNullOrWhiteSpace(fixture.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(fixture.GetProperty("notes").GetString()));
            Assert.NotEmpty(fixture.GetProperty("knownDeltas").EnumerateArray());
        }
    }

    /// <summary>The recorded Esri ImageServer answers carry the documented shape.</summary>
    [Theory]
    [MemberData(nameof(ImageCaseNames))]
    public void The_recorded_image_esri_response_has_its_documented_shape(string name)
    {
        var fixture = ReadFixture(name);
        var operation = Manifest.GetProperty("cases").EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == name)
            .GetProperty("operation").GetString();
        var esri = fixture.GetProperty("esriResponse");
        Assert.True(
            operation switch
            {
                "root" => esri.TryGetProperty("bandCount", out _) || esri.TryGetProperty("pixelType", out _),
                "exportImage" => esri.TryGetProperty("href", out _) || esri.TryGetProperty("contentType", out _),
                "identify" => esri.TryGetProperty("value", out _) || esri.TryGetProperty("location", out _),
                _ => false,
            },
            $"Case '{name}' records no recognisable Esri ImageServer answer shape for '{operation}'.");
    }

    public static IEnumerable<object[]> ImageCaseNames() =>
        Manifest.GetProperty("cases").EnumerateArray()
            .Select(entry => new object[] { entry.GetProperty("name").GetString()! });

    private static void AssertPng(byte[] bytes, int width, int height)
    {
        Assert.True(bytes.Length >= 24, $"PNG body too short ({bytes.Length} bytes) to carry an IHDR.");
        Assert.Equal(PngSignature, bytes[..8]);
        Assert.Equal("IHDR", Encoding.ASCII.GetString(bytes, 12, 4));
        var actualWidth = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var actualHeight = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        Assert.Equal(width, actualWidth);
        Assert.Equal(height, actualHeight);
    }

    private static double Rmse(byte[] first, byte[] second, int width, int height)
    {
        using var a = Image.NewFromBuffer(first);
        using var b = Image.NewFromBuffer(second);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        var sum = 0.0;
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pa = a.Getpoint(x, y);
                var pb = b.Getpoint(x, y);
                Assert.Equal(pa.Length, pb.Length);
                for (var band = 0; band < pa.Length; band++)
                {
                    var diff = pa[band] - pb[band];
                    sum += diff * diff;
                    count++;
                }
            }
        }

        Assert.True(count > 0, "Decoded export has no pixels to compare.");
        return Math.Sqrt(sum / count);
    }

    private static double Range(byte[] bytes, int width, int height)
    {
        using var image = Image.NewFromBuffer(bytes);
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                foreach (var sample in image.Getpoint(x, y))
                {
                    min = Math.Min(min, sample);
                    max = Math.Max(max, sample);
                }
            }
        }

        return max - min;
    }

    private static JsonElement ReadFixture(string name)
    {
        var entry = Manifest.GetProperty("cases").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        return JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureDir, entry.GetProperty("file").GetString()!))).RootElement;
    }

    private static void WriteRaster(string path)
    {
        var pixels = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                pixels[(y * Width) + x] = (byte)((y * 16) + x);
            }
        }

        using var image = Image.NewFromMemory(pixels, Width, Height, 1, Enums.BandFormat.Uchar);
        image.WriteToFile(path);
    }

    private async Task<HttpClient> ImageServiceAsync()
    {
        if (_client is not null)
        {
            return _client;
        }

        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = Service,
            store = "raster",
            services = (string[])["image"],
            layers = new[] { new { dataset = Dataset, layerId = 0, name = Service, kind = "image" } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{Service}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _client = client;
        return client;
    }

    private sealed class ImageReplayFactory(string directory, string token) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", token);
            builder.UseSetting("Spatial:Maps:Path", Path.Combine(directory, "publications.json"));
            builder.UseSetting("Spatial:Raster:Sources:0:Name", Dataset);
            builder.UseSetting("Spatial:Raster:Sources:0:Path", Path.Combine(directory, "wsiearth.tif"));
            builder.UseSetting("Spatial:Raster:Sources:0:Crs", "EPSG:4326");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:0", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:1", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:2", Width.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:3", Height.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeX", "1");
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeY", "1");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:0", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:1", "255");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:2", "82.707");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:3", "39.838");
        }
    }
}
