using Microsoft.AspNetCore.Http;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The Feature Service editing-request parser (spec §9.1.6–§9.1.9): raw
/// feature JSON is kept for per-feature decoding, object-id lists accept the
/// comma and JSON-array forms, and unsupported additions are rejected.
/// </summary>
public sealed class EsriEditRequestTests
{
    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task ParseAdd_reads_the_feature_objects()
    {
        var request = EsriEditRequest.ParseAdd(await ParamsAsync(("features", """[{"attributes":{"name":"x"}},{"attributes":{"name":"y"}}]""")));

        Assert.Equal(2, request.Adds.Count);
        Assert.Empty(request.Updates);
        Assert.Empty(request.Deletes);
        Assert.Null(request.DeleteWhere);
        Assert.False(request.RollbackOnFailure);
    }

    [Fact]
    public async Task ParseAdd_honours_rollback_on_failure()
    {
        var request = EsriEditRequest.ParseAdd(await ParamsAsync(("features", "[]"), ("rollbackOnFailure", "true")));

        Assert.True(request.RollbackOnFailure);
    }

    [Fact]
    public async Task ParseAdd_requires_the_features_parameter()
    {
        var parameters = await ParamsAsync();

        Assert.Throws<EsriInteropException>(() => EsriEditRequest.ParseAdd(parameters));
    }

    [Fact]
    public async Task ParseUpdate_reads_the_feature_objects()
    {
        var request = EsriEditRequest.ParseUpdate(await ParamsAsync(("features", """[{"attributes":{"OBJECTID":1}}]""")));

        Assert.Empty(request.Adds);
        Assert.Single(request.Updates);
    }

    [Fact]
    public async Task ParseDelete_reads_a_comma_separated_object_id_list()
    {
        var request = EsriEditRequest.ParseDelete(await ParamsAsync(("objectIds", "1, 2 ,3")));

        Assert.Equal([1L, 2L, 3L], request.Deletes);
        Assert.Null(request.DeleteWhere);
    }

    [Fact]
    public async Task ParseDelete_reads_a_json_array_of_ids_and_of_objects()
    {
        Assert.Equal([4L, 5L], EsriEditRequest.ParseDelete(await ParamsAsync(("objectIds", "[4,5]"))).Deletes);
        Assert.Equal([6L], EsriEditRequest.ParseDelete(await ParamsAsync(("objectIds", """[{"objectId":6}]"""))).Deletes);
    }

    [Fact]
    public async Task ParseDelete_reads_a_where_clause()
    {
        var clause = ParamsAsync(("where", "name = 'x'"));

        var request = EsriEditRequest.ParseDelete(await clause);

        Assert.Empty(request.Deletes);
        Assert.NotNull(request.DeleteWhere);
    }

    [Fact]
    public async Task ParseDelete_requires_object_ids_or_where()
    {
        var parameters = await ParamsAsync();

        Assert.Throws<EsriInteropException>(() => EsriEditRequest.ParseDelete(parameters));
    }

    [Theory]
    [InlineData("1,two")]
    [InlineData("[1,\"two\"]")]
    [InlineData("[{\"objectId\":\"x\"}]")]
    public async Task ParseDelete_rejects_bad_object_ids(string objectIds)
    {
        var parameters = await ParamsAsync(("objectIds", objectIds));

        Assert.Throws<EsriInteropException>(() => EsriEditRequest.ParseDelete(parameters));
    }

    [Fact]
    public async Task ParseDelete_rejects_a_malformed_where_clause()
    {
        var parameters = await ParamsAsync(("where", "name = "));

        Assert.Throws<EsriInteropException>(() => EsriEditRequest.ParseDelete(parameters));
    }

    [Fact]
    public async Task ParseApply_reads_all_three_lists()
    {
        var request = EsriEditRequest.ParseApply(await ParamsAsync(
            ("adds", """[{"attributes":{"name":"x"}}]"""),
            ("updates", """[{"attributes":{"OBJECTID":2}}]"""),
            ("deletes", "3")));

        Assert.Single(request.Adds);
        Assert.Single(request.Updates);
        Assert.Equal([3L], request.Deletes);
    }

    [Fact]
    public async Task ParseApply_treats_absent_lists_as_empty()
    {
        var request = EsriEditRequest.ParseApply(await ParamsAsync());

        Assert.Empty(request.Adds);
        Assert.Empty(request.Updates);
        Assert.Empty(request.Deletes);
    }

    [Theory]
    [InlineData("gdbVersion")]
    [InlineData("useGlobalIds")]
    [InlineData("returnEditResults")]
    public async Task Unsupported_edit_parameters_are_rejected(string name)
    {
        var parameters = await ParamsAsync((name, "x"));

        Assert.Throws<EsriInteropException>(() => EsriEditRequest.RejectUnsupported(parameters));
    }

    [Fact]
    public async Task Non_array_feature_payload_is_rejected()
    {
        var parameters = await ParamsAsync(("features", """{"attributes":{}}"""));

        Assert.Throws<EsriInteropException>(() => EsriEditRequest.ParseAdd(parameters));
    }

    [Fact]
    public async Task Non_object_feature_entries_are_rejected()
    {
        var parameters = await ParamsAsync(("features", "[1,2]"));

        Assert.Throws<EsriInteropException>(() => EsriEditRequest.ParseAdd(parameters));
    }

    [Fact]
    public async Task Malformed_feature_json_is_rejected_with_the_document_error()
    {
        var parameters = await ParamsAsync(("features", "[{]"));

        Assert.Throws<EsriInteropException>(() => EsriEditRequest.ParseAdd(parameters));
    }

    [Fact]
    public async Task A_non_json_deletes_prefix_is_parsed_as_a_comma_list()
    {
        var request = EsriEditRequest.ParseApply(await ParamsAsync(("deletes", "7,8")));

        Assert.Equal([7L, 8L], request.Deletes);
    }
}
