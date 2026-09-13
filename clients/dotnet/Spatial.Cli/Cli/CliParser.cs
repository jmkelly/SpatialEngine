using System.Globalization;

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
///
/// <para>The catalog is the source of truth for which options take a value:
/// a known value option consumes the next token (including a negative number
/// such as <c>-2</c>), while an unknown option is recorded valueless so it can
/// never swallow a positional. The application reports unknown options after
/// the command is resolved, so a typo cannot silently run a different verb.</para>
/// </summary>
public static class CliParser
{
    // Global and command option names, mapped to whether they are flags. Built
    // once from the catalog so option/value decisions never drift from --help.
    private static readonly Dictionary<string, bool> KnownOptions = BuildKnownOptions();

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

        if (!KnownOptions.TryGetValue(name, out var isFlag))
        {
            // An unknown option must not swallow the next token: leaving it in
            // place keeps the group/verb readable for a precise error.
            Add(options, name, null);
            return index;
        }

        if (isFlag)
        {
            Add(options, name, null);
            return index;
        }

        // A value option consumes the next token unless it is itself an option;
        // a negative number is a value, not an option.
        if (index + 1 < args.Count && IsValue(args[index + 1]))
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

    private static bool IsValue(string token) =>
        !IsOption(token) || IsNegativeNumber(token);

    private static bool IsNegativeNumber(string token) =>
        token.Length > 1
        && token[0] == '-'
        && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

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

    private static Dictionary<string, bool> BuildKnownOptions()
    {
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var option in CliCommandCatalog.GlobalOptions)
        {
            known[option.Name] = option.IsFlag;
        }

        foreach (var command in CliCommandCatalog.Commands)
        {
            foreach (var option in command.Options)
            {
                // A name that takes a value anywhere takes one everywhere.
                known[option.Name] = known.TryGetValue(option.Name, out var isFlag)
                    ? isFlag && option.IsFlag
                    : option.IsFlag;
            }
        }

        return known;
    }
}
