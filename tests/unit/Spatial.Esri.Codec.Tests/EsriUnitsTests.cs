namespace Spatial.Esri.Codec.Tests;

/// <summary>
/// The curated Esri unit-code table: linear codes resolve to metres per
/// unit, angular codes to degrees per unit, unknown codes miss. Factors are
/// pinned to the values verified against the hosted Geometry Service
/// (see <see cref="EsriUnits"/>).
/// </summary>
public sealed class EsriUnitsTests
{
    [Theory]
    [InlineData(9001, 1.0)]
    [InlineData(9002, 0.3048)]
    [InlineData(9003, 0.304800609601219)]
    [InlineData(9005, 0.3047972654)]
    [InlineData(9014, 1.8288)]
    [InlineData(9030, 1852.0)]
    [InlineData(9033, 20.11684023368047)]
    [InlineData(9034, 0.20116840233680472)]
    [InlineData(9035, 1609.347218694437)]
    [InlineData(9036, 1000.0)]
    [InlineData(9093, 1609.344)]
    [InlineData(9096, 0.9144)]
    [InlineData(9097, 20.1168)]
    [InlineData(109001, 0.9144)]
    [InlineData(109002, 0.9144018288036577)]
    public void Known_linear_units_resolve_to_metres(int code, double metres)
    {
        Assert.True(EsriUnits.TryGetLinear(code, out var name, out var metresPerUnit));
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.False(EsriUnits.IsAngular(code));
        Assert.Equal(metres, metresPerUnit, 9);
    }

    [Theory]
    [InlineData(9102, 1.0)]
    public void Known_angular_units_resolve_to_degrees(int code, double degrees)
    {
        Assert.True(EsriUnits.TryGetAngular(code, out var name, out var degreesPerUnit));
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.True(EsriUnits.IsAngular(code));
        Assert.Equal(degrees, degreesPerUnit, 9);
    }

    [Fact]
    public void A_radian_is_180_over_pi_degrees()
    {
        Assert.True(EsriUnits.TryGetAngular(9101, out _, out var degreesPerUnit));

        Assert.Equal(180.0 / Math.PI, degreesPerUnit, 9);
    }

    [Theory]
    [InlineData(424242)]
    [InlineData(9006)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Unknown_codes_miss_both_tables(int code)
    {
        Assert.False(EsriUnits.TryGetLinear(code, out _, out _));
        Assert.False(EsriUnits.TryGetAngular(code, out _, out _));
    }

    [Theory]
    // The REST JS client allowlist research (conformance-sources.md T9)
    // records `units=esriSRUnit_Meter`, so the symbolic spelling a real
    // client sends resolves onto the same curated codes as the numbers.
    [InlineData("esriSRUnit_Meter", 9001)]
    [InlineData("esriSRUnit_Foot", 9002)]
    [InlineData("esriSRUnit_Foot_US", 9003)]
    [InlineData("esriSRUnit_Fathom", 9014)]
    [InlineData("esriSRUnit_NauticalMile", 9030)]
    [InlineData("esriSRUnit_Kilometer", 9036)]
    [InlineData("esriSRUnit_Mile", 9093)]
    [InlineData("esriSRUnit_Yard", 9096)]
    [InlineData("esriSRUnit_Chain", 9097)]
    [InlineData("esriSRUnit_Degree", 9102)]
    [InlineData("esriSRUnit_Radian", 9101)]
    public void A_symbolic_esri_name_resolves_to_its_curated_code(string symbolicName, int expected)
    {
        Assert.True(EsriUnits.TryGetBySymbolicName(symbolicName, out var code));
        Assert.Equal(expected, code);
    }

    [Fact]
    public void A_symbolic_name_is_matched_case_insensitively()
    {
        Assert.True(EsriUnits.TryGetBySymbolicName("esrisrunit_meter", out var code));

        Assert.Equal(9001, code);
    }

    [Theory]
    [InlineData("esriSRUnit_Furlong")]
    [InlineData("esriSRUnit_")]
    [InlineData("Meter")]
    [InlineData("9001")]
    public void A_name_outside_the_curated_table_misses(string symbolicName)
    {
        Assert.False(EsriUnits.TryGetBySymbolicName(symbolicName, out _));
    }

    [Fact]
    public void A_curated_code_reports_its_symbolic_name()
    {
        Assert.True(EsriUnits.TryGetSymbolicName(9001, out var symbolicName));
        Assert.Equal("esriSRUnit_Meter", symbolicName);

        Assert.False(EsriUnits.TryGetSymbolicName(424242, out _));
    }

    [Fact]
    public void The_supported_list_names_the_anchor_codes()
    {
        var supported = EsriUnits.DescribeSupported();

        Assert.Contains("9001", supported, StringComparison.Ordinal);
        Assert.Contains("9102", supported, StringComparison.Ordinal);

        // Both spellings are named, so a client that sent the wrong one is
        // told the right one rather than left guessing.
        Assert.Contains("esriSRUnit_Meter", supported, StringComparison.Ordinal);
    }
}
