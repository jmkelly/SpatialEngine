using System.Text;
using Spatial.Client;
using Spatial.Contracts.Providers;

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
            new("publish", "NAME", "Also register a feature map for the dataset"),
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

    private static async Task<int> AddAsync(CliContext context)
    {
        var (source, fileName) = ResolveSource(context.Arguments);
        var upload = BuildUpload(context, fileName);

        if (context.Settings.DryRun)
        {
            return PlanIngest(context, source, upload);
        }

        var token = context.Settings.Token
            ?? throw new CliUsageException("Admin token required; pass --token or set SPATIAL_ADMIN_TOKEN.");
        var outcome = await context.Gateway.IngestAsync(source, upload, token);
        context.Output.Result(context.Command, outcome, IngestHuman(outcome, context.Settings.Store));
        return ExitCodes.Success;
    }

    private static (string Source, string FileName) ResolveSource(CliArguments arguments)
    {
        var file = arguments.Optional("file");
        var url = arguments.Optional("url");
        if (file is not null && url is not null)
        {
            throw new CliUsageException("Options --file and --url are mutually exclusive.");
        }

        if (file is not null)
        {
            return (file, Path.GetFileName(file));
        }

        if (url is not null)
        {
            return (url, FileNameFromUrl(url));
        }

        throw new CliUsageException("Exactly one of --file or --url is required.");
    }

    private static string FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new CliUsageException($"Option --url expects an absolute URL, got '{url}'.");
        }

        var segment = uri.Segments
            .Select(part => part.Trim('/'))
            .LastOrDefault(part => part.Length > 0);
        return string.IsNullOrEmpty(segment) ? "upload.geojson" : segment;
    }

    private static IngestUpload BuildUpload(CliContext context, string fileName)
    {
        var arguments = context.Arguments;
        var identity = (arguments.Optional("identity") ?? "auto").ToLowerInvariant();
        var identityField = arguments.Optional("identity-field");
        ValidateIdentity(identity, identityField);

        var format = (arguments.Optional("format") ?? "geojson").ToLowerInvariant();
        if (format is not ("geojson" or "ndjson" or "csv"))
        {
            throw new CliUsageException($"Unknown format '{format}'. Use geojson, ndjson or csv.");
        }

        return new IngestUpload(
            fileName,
            arguments.Require("dataset"),
            arguments.RequireInt("srid"),
            format,
            context.Settings.Store,
            identity,
            identityField,
            arguments.Optional("publish"),
            arguments.Has("source-srid") ? arguments.RequireInt("source-srid") : (int?)null);
    }

    private static void ValidateIdentity(string identity, string? identityField)
    {
        if (identity is not ("none" or "auto" or "source"))
        {
            throw new CliUsageException($"Unknown identity '{identity}'. Use none, auto or source.");
        }

        if (identity == "source" && string.IsNullOrEmpty(identityField))
        {
            throw new CliUsageException("--identity source requires --identity-field.");
        }

        if (identity != "source" && !string.IsNullOrEmpty(identityField))
        {
            throw new CliUsageException("--identity-field is only valid with --identity source.");
        }
    }

    private static int PlanIngest(CliContext context, string source, IngestUpload upload)
    {
        var plan = new
        {
            upload.Dataset,
            Source = source,
            upload.Store,
            upload.Format,
            upload.Srid,
            upload.Identity,
            upload.Publish,
        };
        var human = new StringBuilder()
            .Append("Would ingest ").Append(upload.Dataset)
            .Append(" from ").Append(source)
            .Append(" into '").Append(upload.Store).Append("' (")
            .Append(upload.Format).Append(", SRID ").Append(upload.Srid)
            .Append(", identity ").Append(upload.Identity).Append(')');
        if (upload.Publish is { Length: > 0 } publish)
        {
            human.Append(", published as ").Append(publish);
        }

        context.Output.Result(context.Command, plan, human.ToString());
        return ExitCodes.Success;
    }

    private static string IngestHuman(IngestOutcome outcome, string store)
    {
        var text = $"{outcome.Dataset}: {outcome.Features} feature(s) loaded into {store}";
        return outcome.Map is null ? text : $"{text}, published as {outcome.Map.Name}";
    }

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
