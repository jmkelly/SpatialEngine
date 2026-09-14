namespace Spatial.Interop.Esri.Tests;

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

    [Fact]
    public void The_supported_list_names_the_anchor_codes()
    {
        var supported = EsriUnits.DescribeSupported();

        Assert.Contains("9001", supported, StringComparison.Ordinal);
        Assert.Contains("9102", supported, StringComparison.Ordinal);
    }
}
