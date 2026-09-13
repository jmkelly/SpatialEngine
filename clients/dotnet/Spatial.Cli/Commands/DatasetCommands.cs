using System.Text;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>
/// The <c>dataset</c> command group (ADR-0052): list/describe datasets and
/// ingest a file into a new one through the neutral admin API (ADR-0041).
/// </summary>
public static class DatasetCommands
{
    /// <summary>Catalog metadata for this group's commands.</summary>
    public static IReadOnlyList<CliCommandInfo> Specs { get; } =
    [
        new("dataset", "list", "List spatial datasets in a store", "[--pattern LIKE]",
            [new("pattern", "LIKE", "Filter dataset ids with a SQL LIKE pattern")]),
        new("dataset", "describe", "Describe one dataset's fields and geometry", "<dataset>", []),
        new("dataset", "add", "Ingest a file or URL into a new dataset", "",
        [
            new("file", "PATH", "Local file to upload (mutually exclusive with --url)"),
            new("url", "URL", "Remote http(s) file to upload (mutually exclusive with --file)"),
            new("dataset", "schema.table", "Destination dataset id", Required: true),
            new("srid", "EPSG", "SRID of the stored geometry", Required: true),
            new("format", "geojson|ndjson|csv", "Upload format (default geojson)"),
            new("identity", "none|auto|source", "Identity mode (default auto)"),
            new("identity-field", "FIELD", "Integer identity field when --identity source"),
            new("source-srid", "EPSG", "SRID of the file; the engine reprojects it (ADR-0041)"),
            new("publish", "NAME", "Also register a feature publication for the dataset"),
        ]),
    ];

    /// <summary>Runs a <c>dataset</c> verb.</summary>
    public static Task<int> RunAsync(CliContext context) => context.Verb switch
    {
        "list" => ListAsync(context),
        "describe" => DescribeAsync(context),
        "add" => AddAsync(context),
        _ => throw new CliUsageException($"Unknown command '{context.Command}'."),
    };

    private static async Task<int> ListAsync(CliContext context)
    {
        var store = context.Arguments.Optional("store") ?? context.Settings.Store;
        var pattern = context.Arguments.Optional("pattern");
        var datasets = await context.Gateway.ListDatasetsAsync(store, pattern);
        var human = datasets.Count == 0
            ? $"No datasets in '{store}'."
            : string.Join(Environment.NewLine, datasets.Select(dataset => dataset.ToString()));
        context.Output.Result(context.Command, new { store, pattern, datasets }, human);
        return ExitCodes.Success;
    }

    private static async Task<int> DescribeAsync(CliContext context)
    {
        var dataset = context.Arguments.RequirePositional(0, "a dataset id (schema.table)");
        var store = context.Arguments.Optional("store") ?? context.Settings.Store;
        var description = await context.Gateway.DescribeDatasetAsync(dataset, store);
        context.Output.Result(context.Command, new { store, description }, DescribeHuman(description));
        return ExitCodes.Success;
    }

    private static Task<int> AddAsync(CliContext context) =>
        throw new CliUsageException("'dataset add' is not implemented yet.");

    private static string DescribeHuman(DatasetDescription description)
    {
        var builder = new StringBuilder();
        builder.Append(description.Id)
            .Append(" — ").Append(description.GeometryType)
            .Append(' ').Append(description.GeometryColumn)
            .Append(" (SRID ").Append(description.Srid).Append(')')
            .Append(", ").Append(description.EstimatedRowCount).Append(" row(s) est.");
        if (description.IdColumns.Count > 0)
        {
            builder.Append(Environment.NewLine).Append("  identity: ").Append(string.Join(", ", description.IdColumns));
        }

        foreach (var field in description.Schema.Fields)
        {
            builder.Append(Environment.NewLine)
                .Append("  - ").Append(field.Name).Append(": ").Append(field.Kind);
        }

        return builder.ToString();
    }
}
