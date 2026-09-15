using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// T-047 WFS proof corpus: mirrors the T-015 <c>queryAllFeatures</c>
/// closeout for WFS. Replays the QGIS WFS provider page-through (S4:
/// <c>GetFeature&amp;outputFormat=application/json&amp;STARTINDEX=n</c>) and
/// the GDAL WFS driver page-through (S5: the same <c>count</c>/KVP paging,
/// driving the loop off the envelope <c>next</c> links) against a layer
/// larger than <c>MaxFeatures</c> via <c>GetFeature&amp;count=&lt;max&gt;</c>.
/// Each loop must terminate with the exact total in stable order and no
/// duplicates — the same data a real client reads. Depends on the T-046
/// <c>startIndex</c> paging; these loops pass against that landed behaviour
/// with test-only additions (no T-046 rework).
/// </summary>
public sealed class WfsPageThroughTests
{
    private const int MaxFeatures = 3;

    private static readonly string[] Names =
        ["amsterdam", "berlin", "cairo", "dublin", "edinburgh", "frankfurt", "geneva"];

    private static (Map Map, OgcRequestServices Services) Seed()
    {
        var map = OgcFixtures.Map(MapServiceKind.Wfs);
        var (services, store) = OgcFixtures.Build(map);
        for (var index = 0; index < Names.Length; index++)
        {
            store.Seed(OgcFixtures.City(Names[index], 100_000 + index, index, index));
        }

        return (map, services);
    }

    private static async Task<JsonElement> GetFeaturePageAsync(
        Map map, OgcRequestServices services, string query, CancellationToken cancellationToken = default)
    {
        var request = new DefaultHttpContext();
        request.Request.Method = "GET";
        request.Request.Scheme = "http";
        request.Request.Host = new HostString("localhost");
        request.Request.Path = new PathString($"/ogc/{map.Name}/wfs");
        request.Request.QueryString = new QueryString(query);
        var parameters = await OgcParameters.ReadAsync(request, cancellationToken);
        var response = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        response.Response.Body = new MemoryStream();
        var result = await WfsService.HandleAsync(
            map.Name, parameters, services, new OgcOptions { MaxFeatures = MaxFeatures }, request, cancellationToken);
        await result.ExecuteAsync(response);
        response.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(response.Response.Body).ReadToEndAsync(CancellationToken.None);
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>
    /// QGIS WFS provider replay (research/compat/wfs.md S4): the provider
    /// issues <c>GetFeature&amp;outputFormat=application/json</c> pages with
    /// its own <c>STARTINDEX</c> cursor and <c>count</c> at the server max.
    /// The loop must terminate with the exact matched total in stable
    /// feature-id order and no duplicates.
    /// </summary>
    [Fact]
    public async Task Qgis_provider_page_through_terminates_with_the_full_set()
    {
        var (map, services) = Seed();
        Assert.True(Names.Length > MaxFeatures, "The proof layer must exceed MaxFeatures.");

        var collected = new List<string>();
        var startIndex = 0;
        var numberMatched = -1;
        var pages = 0;
        while (true)
        {
            var page = await GetFeaturePageAsync(map, services,
                $"?service=WFS&request=GetFeature&typeNames=Cities&outputFormat=application/json&count={MaxFeatures}&STARTINDEX={startIndex}");
            if (numberMatched < 0)
            {
                numberMatched = page.GetProperty("numberMatched").GetInt32();
            }
            else
            {
                Assert.Equal(numberMatched, page.GetProperty("numberMatched").GetInt32());
            }

            var features = page.GetProperty("features").EnumerateArray().ToArray();
            Assert.Equal(features.Length, page.GetProperty("numberReturned").GetInt32());
            foreach (var feature in features)
            {
                collected.Add(feature.GetProperty("id").GetString()!);
            }

            pages++;
            Assert.True(pages <= Names.Length, "The QGIS page-through loop did not terminate.");
            startIndex += features.Length;
            var hasNext = page.TryGetProperty("next", out _);
            if (features.Length < MaxFeatures || !hasNext)
            {
                break;
            }
        }

        Assert.Equal(Names.Length, numberMatched);
        Assert.Equal(Names.Length, collected.Count);
        Assert.Equal(Names.OrderBy(name => name, StringComparer.Ordinal).Select(name => $"Cities.{name}").ToArray(), collected.ToArray());
        Assert.Equal(collected.Count, collected.Distinct().Count());
    }

    /// <summary>
    /// GDAL WFS driver replay (research/compat/wfs.md S5): the driver pages
    /// with the same <c>count</c>/<c>startIndex</c> KVP pair but drives the
    /// loop off the envelope <c>next</c> links. Following those links must
    /// terminate with the same exact total, order and duplicate-free set as
    /// the provider-cursor loop above.
    /// </summary>
    [Fact]
    public async Task Gdal_driver_page_through_following_next_links_terminates_with_the_full_set()
    {
        var (map, services) = Seed();

        var collected = new List<string>();
        var numberMatched = -1;
        var pages = 0;
        string? query = "?service=WFS&request=GetFeature&typeNames=Cities&outputFormat=application%2Fgeo%2Bjson&count=3&startIndex=0";
        while (query is not null)
        {
            var page = await GetFeaturePageAsync(map, services, query);
            if (numberMatched < 0)
            {
                numberMatched = page.GetProperty("numberMatched").GetInt32();
            }
            else
            {
                Assert.Equal(numberMatched, page.GetProperty("numberMatched").GetInt32());
            }

            var features = page.GetProperty("features").EnumerateArray().ToArray();
            Assert.Equal(features.Length, page.GetProperty("numberReturned").GetInt32());
            foreach (var feature in features)
            {
                collected.Add(feature.GetProperty("id").GetString()!);
            }

            pages++;
            Assert.True(pages <= Names.Length, "The GDAL page-through loop did not terminate.");
            query = page.TryGetProperty("next", out var next)
                ? new Uri(next.GetString()!).Query
                : null;
        }

        Assert.Equal(Names.Length, numberMatched);
        Assert.Equal(3, pages);
        Assert.Equal(Names.Length, collected.Count);
        Assert.Equal(Names.OrderBy(name => name, StringComparer.Ordinal).Select(name => $"Cities.{name}").ToArray(), collected.ToArray());
        Assert.Equal(collected.Count, collected.Distinct().Count());
    }

    /// <summary>
    /// A <c>count</c> above the server max clamps to <c>MaxFeatures</c>:
    /// the first page of a &gt;MaxFeatures layer carries exactly the max
    /// with a <c>next</c> link, so a client asking for everything still
    /// pages instead of swallowing the layer.
    /// </summary>
    [Fact]
    public async Task Count_above_max_features_clamps_to_the_max_on_the_first_page()
    {
        var (map, services) = Seed();

        var page = await GetFeaturePageAsync(map, services,
            "?service=WFS&request=GetFeature&typeNames=Cities&count=1000");

        Assert.Equal(Names.Length, page.GetProperty("numberMatched").GetInt32());
        Assert.Equal(MaxFeatures, page.GetProperty("numberReturned").GetInt32());
        Assert.Equal(MaxFeatures, page.GetProperty("features").GetArrayLength());
        Assert.True(page.TryGetProperty("next", out var next) && next.GetString()!.Contains("startIndex=3"));
    }
}
