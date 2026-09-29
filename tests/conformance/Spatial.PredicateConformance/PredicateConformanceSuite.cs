using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;

namespace Spatial.PredicateConformance;

/// <summary>
/// The shared conformance fixture of ADR-0074: one dataset, one row set and
/// one case list that the in-memory store, the PostGIS store and the SQL
/// Server store all answer. A store joins it by creating
/// <see cref="Dataset"/> and writing <see cref="Rows"/>, then running
/// <see cref="AssertAsync"/>; the assertion is the same code in all three
/// suites, so a second implementation of the vocabulary cannot drift from the
/// reference one without a suite going red.
///
/// <para>
/// The row set is built to make a pushdown <em>tempted</em> to differ from the
/// reference, and its string column carries the case and the punctuation a
/// byte comparison and a locale one order differently (ADR-0123) — the same
/// property ADR-0121 gave the ordering fixture. Every expectation below is the
/// reference's own answer over <see cref="Rows"/>, which is a byte
/// comparison, so a store that inherits its database's collation answers a
/// different set and the suite says so.
/// </para>
/// </summary>
public static class PredicateConformanceSuite
{
    /// <summary>The dataset name the fixture uses (providers namespace their own table).</summary>
    public const string Dataset = "public.predicates";

    /// <summary>The dataset's SRID; every row is a point in it.</summary>
    public const int Srid = 4326;

    /// <summary>The geometry column of the fixture dataset.</summary>
    public const string GeometryColumn = "geometry";

    /// <summary>
    /// The fixture schema: one column per literal kind the vocabulary can
    /// carry, so a case can exercise numbers, strings, booleans, guids and
    /// date-times against real typed columns in every store.
    /// </summary>
    public static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("code", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("score", AttributeKind.Double, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
        new FieldDefinition("reference", AttributeKind.Guid, nullable: true),
        new FieldDefinition("seen", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition(GeometryColumn, AttributeKind.Geometry),
    ]);

    private static readonly DateTimeOffset Seen = DateTimeOffset.Parse(
        "2023-11-14T22:13:20Z", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly Guid Reference = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>
    /// The fixture rows. The codes carry <em>case</em> (<c>Delta</c> beside
    /// <c>delta</c>) and <em>punctuation</em> (<c>_bravo</c>), because that is
    /// what separates a byte comparison from a locale one: the contract
    /// compares strings ordinally — <c>C</c> orders
    /// <c>Delta, _bravo, alpha, beta, delta, epsilon, gamma</c> — while
    /// <c>en_US.utf8</c> orders them
    /// <c>alpha, beta, _bravo, delta, Delta, epsilon, gamma</c>, and a
    /// case-insensitive collation (SQL Server's own default) folds
    /// <c>Delta</c> onto <c>delta</c> entirely. A fixture of lower-case words
    /// in alphabetical order cannot see any of that: every collation in use
    /// agrees with a byte comparison on them, so a pushed <c>WHERE</c> that
    /// inherits the database's collation went unmeasured (ADR-0098 §3,
    /// ADR-0121, ADR-0123).
    ///
    /// <para>
    /// The two codes that differ under a locale collation sit outside the
    /// bounding box the box cases use, so those cases still measure the box
    /// and nothing else, and the <c>LIKE</c> cases still match a set that is
    /// the same under both orders. The null row pins null semantics.
    /// </para>
    /// </summary>
    public static Feature[] Rows { get; } =
    [
        Row("alpha", 3_664_000, 1.5, true, Reference, Seen, 13.4, 52.5),
        Row("beta", 2_000_000, 2.5, false, Reference, Seen.AddDays(-400), 2.35, 48.85),
        Row("gamma", 900_000, 0.5, true, null, null, -0.12, 51.5),
        Row("delta", null, null, null, Reference, null, 12.5, 41.9),
        Row("epsilon", 9_007_199_254_740_993L, 3.5, true, Reference, Seen, 13.0, 52.0),
        Row("_bravo", 1_200_000, null, false, null, null, 5.0, 5.0),
        Row("Delta", null, null, null, null, null, 5.5, 5.5),
    ];

    /// <summary>
    /// The defining batch a store creates the dataset from: the schema plus
    /// one row, because a store resolves the geometry column's type from the
    /// sample. No store inserts it — every suite writes
    /// <see cref="Rows"/> itself, so the three answer the same question about
    /// the same rows.
    /// </summary>
    public static FeatureBatch Sample { get; } = new(Schema, [Rows[0]]);

    /// <summary>
    /// The cases. Between them they cover every operator, every literal kind,
    /// null semantics, the logical operators, membership, <c>LIKE</c>, and a
    /// bounding box combined with a filter. Every expectation is the answer over
    /// <see cref="Rows"/> itself: a case that selects nothing is as load-bearing
    /// as one that selects a row, because "no row has exactly this score" is
    /// what proves a whole-number literal is compared and not coerced.
    /// </summary>
    public static IReadOnlyList<PredicateCase> Cases { get; } =
    [
        new("equality on a string", "code = 'alpha'", ["alpha"]),
        new("inequality on a string", "code != 'alpha'", ["Delta", "_bravo", "beta", "delta", "epsilon", "gamma"]),
        new("both inequality spellings agree", "code <> 'alpha'", ["Delta", "_bravo", "beta", "delta", "epsilon", "gamma"]),
        // The two ordering cases and the range below are the ones a pushed
        // `WHERE` gets wrong without a collation of its own: the answer is the
        // byte order of the codes, and a locale collation puts `Delta` and
        // `_bravo` on the other side of the bound (ADR-0123).
        new("ordering on a string", "code < 'delta'", ["Delta", "_bravo", "alpha", "beta"]),
        new("ordering on a string, inclusive", "code <= 'delta'", ["Delta", "_bravo", "alpha", "beta", "delta"]),
        new(
            "a case and a punctuation-leading code in one range",
            "code >= '_bravo' AND code <= 'delta'",
            ["_bravo", "alpha", "beta", "delta"]),
        new("ordering on a number", "population > 2000000", ["alpha", "epsilon"]),
        new("ordering on a number, fractional", "score >= 1.5 AND score < 3.5", ["alpha", "beta"]),
        new("a whole number past double precision", "population = 9007199254740993", ["epsilon"]),
        new("a whole number is never rounded against a fractional column", "score = 2", []),
        new("a negative bound on a fractional column", "score > -1", ["alpha", "beta", "epsilon", "gamma"]),
        new("equality on a boolean", "active = TRUE", ["alpha", "gamma", "epsilon"]),
        new("equality on a guid", $"reference = '{Reference}'", ["alpha", "beta", "delta", "epsilon"]),
        new("a null value satisfies no comparison", "population > 0", ["alpha", "_bravo", "beta", "epsilon", "gamma"]),
        new("a null value is not unequal either", "population != 900000", ["alpha", "_bravo", "beta", "epsilon"]),
        new("null test", "population IS NULL", ["Delta", "delta"]),
        new("negated null test", "population IS NOT NULL", ["alpha", "_bravo", "beta", "epsilon", "gamma"]),
        new("a date-time literal", "seen >= TIMESTAMP '2023-01-01 00:00:00'", ["alpha", "epsilon"]),
        new("a date-time range", "seen > TIMESTAMP '2022-01-01 00:00:00' AND seen < TIMESTAMP '2023-01-01 00:00:00'", ["beta"]),
        // A date-time is an instant, so a number is the same axis: this is the
        // case that stops a back end deciding for itself that a numeric literal
        // cannot touch a timestamp column and answering "no rows".
        new("a number against a date-time column", "seen = 1700000000000", ["alpha", "epsilon"]),
        new("like with a wildcard suffix", "code LIKE 'a%'", ["alpha"]),
        // `_` is one character whatever it stands for, so this pattern answers
        // the same set under every collation: a case-folding one still matches
        // the upper-case code, which is what a `LIKE` under `en_US.utf8`
        // answers too.
        new("like with a single-character wildcard", "code LIKE '_elta'", ["Delta", "delta"]),
        new("like is a whole-value test", "code LIKE 'lph'", []),
        new("conjunction", "code = 'alpha' AND population = 3664000", ["alpha"]),
        new("disjunction", "code = 'alpha' OR code = 'beta'", ["alpha", "beta"]),
        new("parenthesised precedence", "code = 'alpha' OR (population >= 2000000 AND active = FALSE)", ["alpha", "beta"]),
        new("membership", "code IN ('alpha', 'gamma')", ["alpha", "gamma"]),
        new("negated membership", "code NOT IN ('alpha', 'gamma')", ["Delta", "_bravo", "beta", "delta", "epsilon"]),
        new(
            "membership in codes a case-insensitive collation folds together",
            "code IN ('Delta', 'delta')",
            ["Delta", "delta"]),
        new(
            "a bounding box with a filter that keeps everything",
            "code LIKE '%'",
            ["alpha", "delta", "epsilon"],
            new BoundingBox(12.0, 41.0, 14.0, 53.0)),
        new(
            "a bounding box and a filter together",
            "population > 0",
            ["alpha", "epsilon"],
            new BoundingBox(12.0, 41.0, 14.0, 53.0)),
    ];

    /// <summary>
    /// Runs every case against a store and reports the ones that answered
    /// differently. A suite asserts the result is empty, so a failure names
    /// the case, the filter and both answers.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AssertAsync(
        IFeatureStore store, string dataset, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        foreach (var testCase in Cases)
        {
            var plan = new FeatureQuery(
                Where: FeatureFilter.Parse(testCase.Filter),
                BoundingBox: testCase.BoundingBox is { } box
                    ? new Spatial.Contracts.BoundingBox(box.MinX, box.MinY, box.MaxX, box.MaxY)
                    : null);
            var page = await store.QueryAsync(dataset, plan, cancellationToken);
            var codes = page.Features
                .Select(feature => feature["code"].StringValue)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var expected = testCase.ExpectedCodes.Order(StringComparer.Ordinal).ToArray();
            if (!codes.SequenceEqual(expected, StringComparer.Ordinal))
            {
                failures.Add($"{testCase.Name}: filter '{testCase.Filter}' selected [{string.Join(", ", codes)}] instead of [{string.Join(", ", expected)}]");
            }
        }

        return failures;
    }

    /// <summary>
    /// Runs every case against a predicate <em>evaluator</em> rather than a
    /// store, for the implementations that evaluate a plan in process behind a
    /// store surface the fixture cannot create a dataset on (the demo
    /// catalogue, the facade's residual filter). The cases and the rows are the
    /// same ones the store-level run uses, so the three copies of the
    /// reference semantics cannot drift apart silently.
    /// </summary>
    /// <param name="evaluator">Whether the predicate holds for the feature.</param>
    /// <param name="inBox">Whether the row falls inside a case's bounding box.</param>
    public static IReadOnlyList<string> AssertEvaluator(
        Func<Predicate, Feature, bool> evaluator, Func<Feature, BoundingBox, bool>? inBox = null)
    {
        var failures = new List<string>();
        foreach (var testCase in Cases)
        {
            var predicate = FeatureFilter.Parse(testCase.Filter)!;
            var codes = Rows
                .Where(row => testCase.BoundingBox is not { } box || (inBox ?? Default)(row, box))
                .Where(row => evaluator(predicate, row))
                .Select(row => row["code"].StringValue)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var expected = testCase.ExpectedCodes.Order(StringComparer.Ordinal).ToArray();
            if (!codes.SequenceEqual(expected, StringComparer.Ordinal))
            {
                failures.Add($"{testCase.Name}: filter '{testCase.Filter}' selected [{string.Join(", ", codes)}] instead of [{string.Join(", ", expected)}]");
            }
        }

        return failures;
    }

    /// <summary>The fixture's own bounding-box test: the row's point is inside the box.</summary>
    private static bool Default(Feature row, BoundingBox box)
    {
        if (row[GeometryColumn].GeometryValue.Envelope is not { } envelope)
        {
            return false;
        }

        return envelope.MinX <= box.MaxX && envelope.MaxX >= box.MinX
            && envelope.MinY <= box.MaxY && envelope.MaxY >= box.MinY;
    }

    private static Feature Row(
        string code,
        long? population,
        double? score,
        bool? active,
        Guid? reference,
        DateTimeOffset? seen,
        double x,
        double y) => new(
        new FeatureId(code),
        Schema,
        [
            AttributeValue.FromString(code),
            population is { } count ? AttributeValue.FromInt64(count) : AttributeValue.Null,
            score is { } value ? AttributeValue.FromDouble(value) : AttributeValue.Null,
            active is { } flag ? AttributeValue.FromBoolean(flag) : AttributeValue.Null,
            reference is { } id ? AttributeValue.FromGuid(id) : AttributeValue.Null,
            seen is { } moment ? AttributeValue.FromDateTimeOffset(moment) : AttributeValue.Null,
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(Srid))),
        ]);
}
