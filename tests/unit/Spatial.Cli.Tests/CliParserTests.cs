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

    [Theory]
    [InlineData("-2")]
    [InlineData("-0.5")]
    [InlineData("-Infinity")]
    public void Parse_consumes_a_negative_number_as_an_option_value(string value)
    {
        var parsed = CliParser.Parse(["map", "set-style", "--opacity", value]);

        Assert.Equal("map", parsed.Group);
        Assert.Equal("set-style", parsed.Verb);
        Assert.Equal(value, parsed.Last("opacity"));
    }

    [Fact]
    public void Parse_does_not_consume_a_positional_for_an_unknown_option()
    {
        var parsed = CliParser.Parse(["--bogus", "map", "list"]);

        Assert.Equal("map", parsed.Group);
        Assert.Equal("list", parsed.Verb);
        Assert.True(parsed.Has("bogus"));
    }
}
