using System.Globalization;

namespace Spatial.Cli;

/// <summary>
/// Typed, validated access to one command's parsed arguments (ADR-0052).
/// Missing required options and malformed values fail with
/// <see cref="CliUsageException"/>, which the application maps to the usage
/// exit code; commands never see a raw dictionary.
/// </summary>
public sealed class CliArguments
{
    private readonly ParsedCommandLine _parsed;
    private readonly CliCommandInfo _command;

    /// <summary>Binds a parsed command line to its catalog entry.</summary>
    public CliArguments(ParsedCommandLine parsed, CliCommandInfo command)
    {
        _parsed = parsed;
        _command = command;
    }

    /// <summary>Positional arguments after group and verb.</summary>
    public IReadOnlyList<string> Positionals => _parsed.Positionals;

    /// <summary>Whether the option was supplied (as a flag or with a value).</summary>
    public bool Has(string name) => _parsed.Has(name);

    /// <summary>The option value, or <see langword="null"/> when absent or valueless.</summary>
    public string? Optional(string name) => _parsed.Last(name);

    /// <summary>Every value supplied for a repeatable option, in order.</summary>
    public IReadOnlyList<string> All(string name) =>
        _parsed.Options.TryGetValue(name, out var values)
            ? values.Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToArray()
            : [];

    /// <summary>The option value, failing when it was not supplied.</summary>
    public string Require(string name) =>
        Optional(name) is { Length: > 0 } value ? value : throw Missing(name);

    /// <summary>An integer option, or <paramref name="fallback"/> when absent.</summary>
    public int OptionalInt(string name, int fallback) =>
        Optional(name) is { Length: > 0 } value ? ParseInt(name, value) : fallback;

    /// <summary>A required integer option.</summary>
    public int RequireInt(string name) => ParseInt(name, Require(name));

    /// <summary>A floating-point option, or <paramref name="fallback"/> when absent.</summary>
    public double OptionalDouble(string name, double fallback) =>
        Optional(name) is { Length: > 0 } value ? ParseDouble(name, value) : fallback;

    /// <summary>A required floating-point option.</summary>
    public double RequireDouble(string name) => ParseDouble(name, Require(name));

    /// <summary>A required positional argument.</summary>
    public string RequirePositional(int index, string description) =>
        index < Positionals.Count ? Positionals[index] : throw new CliUsageException(
            $"Missing {description}. See 'spatial {_command.Group} {_command.Verb} --help'.");

    /// <summary>An optional positional argument.</summary>
    public string? OptionalPositional(int index) => index < Positionals.Count ? Positionals[index] : null;

    private CliUsageException Missing(string name) =>
        new($"Missing required option --{name}. See 'spatial {_command.Group} {_command.Verb} --help'.");

    private static int ParseInt(string name, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new CliUsageException($"Option --{name} expects an integer, got '{value}'.");

    private static double ParseDouble(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new CliUsageException($"Option --{name} expects a number, got '{value}'.");
}
