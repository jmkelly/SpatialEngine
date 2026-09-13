namespace Spatial.Cli;

/// <summary>
/// The raw result of parsing a command line: the command's group and verb,
/// its positional arguments, and the long options as a name → values map
/// (<see langword="null"/> value means a flag was present). Repeated options
/// keep every value. No validation happens here; <see cref="CliArguments"/>
/// validates against the command catalog (ADR-0052).
/// </summary>
public sealed record ParsedCommandLine(
    string? Group,
    string? Verb,
    IReadOnlyList<string> Positionals,
    IReadOnlyDictionary<string, IReadOnlyList<string?>> Options,
    bool WantsHelp)
{
    /// <summary>Whether the option was supplied at least once.</summary>
    public bool Has(string name) => Options.ContainsKey(name);

    /// <summary>The last value supplied for the option, or null when absent/valueless.</summary>
    public string? Last(string name) => Options.TryGetValue(name, out var values) ? values[^1] : null;
}

/// <summary>
/// A tiny, dependency-free argument parser with descriptive long options
/// (ADR-0052). Supports <c>--name value</c>, <c>--name=value</c>, repeated
/// options, boolean flags from the known flag set, <c>-h</c>/<c>--help</c>,
/// and <c>--</c> to end option parsing.
/// </summary>
public static class CliParser
{
    // Options that are flags everywhere they appear: they never consume the
    // next token as a value. Keep this list to genuinely boolean switches.
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "help",
        "json",
        "quiet",
        "verbose",
        "dry-run",
        "hidden",
        "visible",
        "force",
        "no-verify",
        "include-style",
    };

    /// <summary>Parses <paramref name="args"/> into group, verb, positionals and options.</summary>
    public static ParsedCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        var positionals = new List<string>();
        var endOfOptions = false;

        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (endOfOptions || !IsOption(token))
            {
                positionals.Add(token);
                continue;
            }

            if (token == "--")
            {
                endOfOptions = true;
                continue;
            }

            index = Consume(token, args, index, options);
        }

        var frozen = options.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string?>)pair.Value,
            StringComparer.Ordinal);
        return new ParsedCommandLine(
            positionals.Count > 0 ? positionals[0] : null,
            positionals.Count > 1 ? positionals[1] : null,
            positionals.Count > 2 ? positionals[2..] : [],
            frozen,
            options.ContainsKey("help"));
    }

    private static int Consume(string token, IReadOnlyList<string> args, int index, Dictionary<string, List<string?>> options)
    {
        var (name, inlineValue) = Split(token);
        if (name.Length == 0)
        {
            throw new CliUsageException("Unrecognised option '--'. Use '--name value' or '--name=value'.");
        }

        if (name == "h")
        {
            Add(options, "help", null);
            return index;
        }

        if (inlineValue is not null)
        {
            Add(options, name, inlineValue);
            return index;
        }

        if (Flags.Contains(name))
        {
            Add(options, name, null);
            return index;
        }

        // An option that takes a value consumes the next token unless it is
        // itself an option (then the command's required-option check reports it).
        if (index + 1 < args.Count && !IsOption(args[index + 1]))
        {
            Add(options, name, args[index + 1]);
            return index + 1;
        }

        Add(options, name, null);
        return index;
    }

    private static void Add(Dictionary<string, List<string?>> options, string name, string? value)
    {
        if (!options.TryGetValue(name, out var values))
        {
            values = [];
            options[name] = values;
        }

        values.Add(value);
    }

    private static bool IsOption(string token) =>
        token.Length > 1 && token[0] == '-' && token != "-";

    private static (string Name, string? Value) Split(string token)
    {
        var body = token.StartsWith("--", StringComparison.Ordinal) ? token[2..] : token[1..];
        var separator = body.IndexOf('=', StringComparison.Ordinal);
        if (separator < 0)
        {
            return (body, null);
        }

        return (body[..separator], body[(separator + 1)..]);
    }
}
