using Spatial.Cli;
using Spatial.Client;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Cli.Tests;

/// <summary>
/// Unit tests of <c>dataset query</c> (ADR-0158): the options the verb
/// accepts, the plan it builds, the page it prints, and the rule that the
/// deprecated <c>--filter</c> text and <c>--where</c> are the same plan. No
/// network — the gateway is faked.
/// </summary>
public sealed class DatasetQueryCommandTests
{
    private static FakeSpatialGateway Gateway() => new()
    {
        Page = new FeatureQueryPage([], null, 0, false),
    };

    [Fact]
    public async Task Query_builds_the_plan_from_the_options()
    {
        var gateway = Gateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "dataset", "query", "public.places",
            "--where", "population > 1000000",
            "--bbox", "0,0,10,10",
            "--project", "name,population",
            "--order", "name:desc",
            "--limit", "2",
            "--offset", "1",
            "--store", "memory");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var call = Assert.Single(gateway.QueryCalls);
        Assert.Equal("public.places", call.Dataset);
        Assert.Equal("memory", call.Store);
        var plan = call.Plan;
        Assert.NotNull(plan.Where);
        Assert.Equal("population", plan.Where!.Fields()[0].Name);
        Assert.Equal(new BoundingBox(0, 0, 10, 10), plan.BoundingBox);
        Assert.Equal(["name", "population"], plan.Projection);
        Assert.Equal(new OrderTerm("name", SortDirection.Descending), Assert.Single(plan.Order!));
        Assert.Equal(2, plan.Limit);
        Assert.Equal(1, plan.Offset);
    }

    [Fact]
    public async Task Filter_and_where_are_the_same_plan()
    {
        var gateway = Gateway();

        Assert.Equal(ExitCodes.Success, (await CliHarness.RunAsync(
            gateway, "dataset", "query", "public.places", "--filter", "population > 1000000")).ExitCode);
        Assert.Equal(ExitCodes.Success, (await CliHarness.RunAsync(
            gateway, "dataset", "query", "public.places", "--where", "population > 1000000")).ExitCode);

        Assert.Equal(2, gateway.QueryCalls.Count);
        Assert.Equal(gateway.QueryCalls[0].Plan.Where, gateway.QueryCalls[1].Plan.Where);
    }

    [Fact]
    public async Task Query_reports_the_page()
    {
        var gateway = Gateway();
        gateway.Page = new FeatureQueryPage(
            [new FeatureBatch(
                new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]),
                [new Feature(new FeatureId("1"), new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]), [AttributeValue.FromString("Berlin")])])],
            "next-token",
            5,
            true);

        var run = await CliHarness.RunAsync(gateway, "dataset", "query", "public.places", "--limit", "1");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("5", run.Output, StringComparison.Ordinal);
        Assert.Contains("next-token", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_rejects_a_malformed_order_term()
    {
        var run = await CliHarness.RunAsync(Gateway(), "dataset", "query", "public.places", "--order", "name:sideways");

        Assert.NotEqual(ExitCodes.Success, run.ExitCode);
        Assert.Contains("order", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Query_rejects_a_malformed_bbox()
    {
        var run = await CliHarness.RunAsync(Gateway(), "dataset", "query", "public.places", "--bbox", "0,0,10");

        Assert.NotEqual(ExitCodes.Success, run.ExitCode);
        Assert.Contains("bbox", run.Error, StringComparison.OrdinalIgnoreCase);
    }
}
