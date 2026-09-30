using Spatial.Core.Features.Query;
using Spatial.Stores.ArcGisRest;

namespace Spatial.Stores.ArcGisRest.Tests;

/// <summary>
/// The remote <c>where</c> a plan is rendered into, and the one comparison the
/// remote grammar has no operator for (ADR-0132).
///
/// <para>
/// The vocabulary's case-folding text comparison has no Esri <c>where</c>
/// spelling — ArcGIS has no <c>ILIKE</c>, and its <c>LIKE</c> is
/// case-sensitivity-by-deployment — so a plan carrying one cannot be stated to
/// the remote. It is dropped rather than refused: the remote restriction is a
/// pre-filter over a read this store finishes with the reference executor, so a
/// dropped term admits more rows and never fewer, and the answer is still the
/// reference's. Anything else would turn a readable remote layer into a
/// <c>invalid.arguments</c> for a filter the caller never sent.
/// </para>
/// </summary>
public sealed class ArcGisRestFoldedWhereTests
{
    [Fact]
    public void A_plan_that_is_only_a_folded_comparison_restricts_nothing_remotely()
    {
        // The whole layer is read and the reference executor finishes the plan
        // over what came back, which is the one shape in which the answer is
        // still the engine's answer.
        Assert.Null(Where("code ILIKE 'ALPH%'"));
    }

    [Fact]
    public void A_folded_comparison_beside_a_clause_the_remote_can_state_keeps_that_clause()
    {
        var rendered = Where("code ILIKE 'ALPH%' AND population > 1000");

        Assert.NotNull(rendered);
        Assert.Contains("population > 1000", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ILIKE", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_folded_comparison_in_a_disjunction_is_dropped_and_the_rest_survives()
    {
        // A disjunction of two folded patterns is all folded: nothing is
        // left to ask the remote, so nothing is asked.
        Assert.Null(Where("code ILIKE 'ALPH%' OR label ILIKE 'URBAN%'"));
    }

    [Fact]
    public void A_plan_without_one_is_untouched()
    {
        // The pre-existing shapes are the remote service's own, byte for byte.
        Assert.Equal("code LIKE 'alpha%'", Where("code LIKE 'alpha%'"));
        Assert.Equal("(code = 'alpha' OR code = 'beta')", Where("code = 'alpha' OR code = 'beta'"));
    }

    private static string? Where(string filter)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return ArcGisRestMapper.RenderWhere(predicate);
    }
}
