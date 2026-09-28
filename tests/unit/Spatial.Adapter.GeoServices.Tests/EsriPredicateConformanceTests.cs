using Spatial.Core.Features;
using Spatial.PredicateConformance;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The facade's per-feature evaluation of the core predicate vocabulary — the
/// residual path behind a store pushdown (ADR-0074 §4) — held to the same
/// conformance cases every store answers, so the facade's copy of the
/// reference semantics cannot drift from the stores'.
/// </summary>
public sealed class EsriPredicateConformanceTests
{
    [Fact]
    public void The_facade_evaluator_answers_every_conformance_case()
    {
        var failures = PredicateConformanceSuite.AssertEvaluator(
            (predicate, feature) => EsriPredicateEvaluator.Matches(predicate, feature));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
