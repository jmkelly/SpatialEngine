using Spatial.PredicateConformance;
using Spatial.Stores.Demo;

namespace Spatial.Stores.Demo.Tests;

/// <summary>
/// The demo store's in-process evaluation of the one predicate vocabulary
/// (ADR-0074), held to the same conformance cases the memory, PostGIS and SQL
/// Server stores answer: the demo catalogue is generated rather than created
/// per test, so the cases run against the fixture rows through the evaluator
/// the store uses.
/// </summary>
public sealed class DemoPredicateConformanceTests
{
    [Fact]
    public void The_demo_evaluator_answers_every_conformance_case()
    {
        var failures = PredicateConformanceSuite.AssertEvaluator(DemoPredicate.Matches);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
