using System.Text;
using Spatial.Client;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

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
        new("dataset", "query", "Read a dataset with a feature-query plan and report the page", "<dataset>",
        [
            new("where", "TEXT", "Attribute filter text for the plan's where (one predicate grammar, ADR-0074)"),
            new("filter", "TEXT", "Deprecated alias of --where, the spelling clients sent before ADR-0158"),
            new("bbox", "X,Y,X,Y", "Bounding-box pre-filter, minx,miny,maxx,maxy"),
            new("project", "A,B", "Comma-separated schema fields the returned features carry"),
            new("order", "FIELD[:asc|:desc],…", "Sort keys, comma separated; the identity is the tie-break"),
            new("ids", "ID,ID,…", "Comma-separated feature identities to select"),
            new("limit", "N", "Maximum number of features in the page"),
            new("offset", "N", "Number of features to skip"),
            new("cursor", "TOKEN", "Continuation token from a previous page's nextCursor"),
        ]),
        new("dataset", "add", "Ingest a file or URL into a new dataset", "",
        [
            new("file", "PATH", "Local file to upload (mutually exclusive with --url)"),
            new("url", "URL", "Remote http(s) file to upload (mutually exclusive with --file)"),
            new("dataset", "schema.table", "Destination dataset id", Required: true),
            new("srid", "EPSG", "SRID of the stored geometry", Required: true),
            new("format", "geojson|ndjson|csv", "Upload format (default geojson)"),
            new("identity", "auto|source", "Identity mode (default auto)"),
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
        "query" => QueryAsync(context),
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

    private static async Task<int> QueryAsync(CliContext context)
    {
        var dataset = context.Arguments.RequirePositional(0, "a dataset id (schema.table)");
        var store = context.Arguments.Optional("store") ?? context.Settings.Store;
        var plan = BuildPlan(context.Arguments);

        var page = await context.Gateway.QueryFeaturesAsync(dataset, plan, store);

        var features = page.Features.Count();
        context.Output.Result(
            context.Command,
            new
            {
                store,
                dataset,
                Features = features,
                page.TotalCount,
                page.HasMore,
                page.NextCursor,
            },
            QueryHuman(features, page));
        return ExitCodes.Success;
    }

    /// <summary>
    /// The plan the options spell (ADR-0158 §6). <c>--where</c> and the
    /// deprecated <c>--filter</c> compile the same text into the same predicate
    /// through the one boundary parser (ADR-0074 §3), so a script that used the
    /// old option keeps asking the same question.
    /// </summary>
    private static FeatureQuery BuildPlan(CliArguments arguments)
    {
        var where = FeatureFilter.Parse(arguments.Optional("where") ?? arguments.Optional("filter"));
        var bbox = ParseBbox(arguments.Optional("bbox"));
        var projection = Split(arguments.Optional("project"));
        var order = Split(arguments.Optional("order"))?.Select(OrderTerm).ToArray();
        var ids = Split(arguments.Optional("ids"))?.Select(id => new FeatureId(id)).ToArray();
        var limit = arguments.Optional("limit") is { Length: > 0 } cap ? arguments.RequireInt("limit") : (int?)null;
        var offset = arguments.Optional("offset") is { Length: > 0 } skip ? arguments.RequireInt("offset") : (int?)null;
        return new FeatureQuery(ids, where, bbox, projection, order, limit, offset, arguments.Optional("cursor"));
    }

    private static BoundingBox? ParseBbox(string? text)
    {
        var parts = Split(text);
        if (parts is null)
        {
            return null;
        }

        if (parts.Length != 4)
        {
            throw new CliUsageException("Option --bbox expects minx,miny,maxx,maxy — four numbers.");
        }

        var values = new double[4];
        for (var index = 0; index < values.Length; index++)
        {
            if (!double.TryParse(parts[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out values[index])
                || double.IsNaN(values[index])
                || double.IsInfinity(values[index]))
            {
                throw new CliUsageException($"Option --bbox expects four finite numbers, got '{text}'.");
            }
        }

        return new BoundingBox(values[0], values[1], values[2], values[3]);
    }

    private static OrderTerm OrderTerm(string term)
    {
        var parts = term.Split(':', 2);
        var direction = parts.Length == 1 ? "asc" : parts[1].Trim().ToLowerInvariant();
        if (parts[0].Length == 0)
        {
            throw new CliUsageException($"Option --order expects 'field' or 'field:asc'/'field:desc', got '{term}'.");
        }

        return direction switch
        {
            "asc" or "ascending" => new OrderTerm(parts[0], SortDirection.Ascending),
            "desc" or "descending" => new OrderTerm(parts[0], SortDirection.Descending),
            _ => throw new CliUsageException(
                $"Option --order expects 'asc' or 'desc' as the direction, got '{parts[1]}' in '{term}'."),
        };
    }

    /// <summary>A comma-separated option's trimmed values, or <c>null</c> when it was not sent.</summary>
    private static string[]? Split(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : [.. text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static string QueryHuman(int features, FeatureQueryPage page)
    {
        var text = new StringBuilder()
            .Append(features).Append(" feature(s)");
        if (page.TotalCount is { } total)
        {
            text.Append(" of ").Append(total).Append(" matched");
        }

        if (page.HasMore)
        {
            text.Append("; more remain — continue with --cursor ")
                .Append(page.NextCursor ?? "(no token was issued)");
        }

        return text.ToString();
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
        if (identity == "none")
        {
            // ADR-0149: every ingested dataset carries an identity column.
            throw new CliUsageException("Identity 'none' is not supported: every ingested dataset is keyed. Use auto or source.");
        }

        if (identity is not ("auto" or "source"))
        {
            throw new CliUsageException($"Unknown identity '{identity}'. Use auto or source.");
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
