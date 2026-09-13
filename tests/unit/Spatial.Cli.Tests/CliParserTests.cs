using Spatial.Cli;

namespace Spatial.Cli.Tests;

/// <summary>The dependency-free parser's option and positional handling (ADR-0052).</summary>
public sealed class CliParserTests
{
    [Fact]
    public void Parse_reads_group_verb_and_positionals()
    {
        var parsed = CliParser.Parse(["dataset", "describe", "public.world"]);

        Assert.Equal("dataset", parsed.Group);
        Assert.Equal("describe", parsed.Verb);
        Assert.Equal(["public.world"], parsed.Positionals);
    }

    [Fact]
    public void Parse_supports_spaced_and_inline_values()
    {
        var parsed = CliParser.Parse(["map", "show", "World", "--host", "http://x", "--timeout=5"]);

        Assert.Equal("http://x", parsed.Last("host"));
        Assert.Equal("5", parsed.Last("timeout"));
    }

    [Fact]
    public void Parse_keeps_repeated_option_values_in_order()
    {
        var parsed = CliParser.Parse(["map", "create", "--layer", "a.x", "--layer", "b.y"]);

        Assert.Equal(["a.x", "b.y"], parsed.Options["layer"]);
    }

    [Fact]
    public void Parse_recognises_flags_without_values()
    {
        var parsed = CliParser.Parse(["map", "create", "--dry-run", "--force", "extra"]);

        Assert.True(parsed.Has("dry-run"));
        Assert.True(parsed.Has("force"));
        Assert.Equal(["extra"], parsed.Positionals);
    }

    [Fact]
    public void Parse_treats_short_h_as_help()
    {
        Assert.True(CliParser.Parse(["map", "create", "-h"]).WantsHelp);
    }

    [Fact]
    public void Parse_stops_option_parsing_at_double_dash()
    {
        var parsed = CliParser.Parse(["dataset", "list", "--", "--not-an-option"]);

        Assert.Equal(["--not-an-option"], parsed.Positionals);
        Assert.False(parsed.Has("not-an-option"));
    }
}
