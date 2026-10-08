using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Contracts.Http;

/// <summary>
/// The JSON wire of a feature-read plan on <c>POST /api/features/query</c>
/// (ADR-0158 §1): the ADR-0074 <see cref="FeatureQuery"/> under the same
/// camelCase names its members have, so a client that reads the plan's
/// documentation reads the request. Every member is the core value it stands
/// for, expressed in boundary DTOs because the core types are not JSON shapes
/// (a <see cref="Predicate"/> is a tree of cases, not an object).
///
/// <para>
/// This is the <em>plan</em> spelling. The top-level <c>bbox</c> and
/// <c>filter</c> of <see cref="FeatureQueryRequest"/> are sugar for
/// <see cref="Bbox"/> and <see cref="Where"/> and stay for one more release;
/// a request that sends both spellings of one member must agree
/// (ADR-0158 §2).
/// </para>
/// </summary>
/// <param name="Ids">The feature identities to select, or <c>null</c> for every identity.</param>
/// <param name="Where">The predicate tree, or <c>null</c> for no attribute restriction.</param>
/// <param name="Bbox">The bounding-box pre-filter, or <c>null</c> for no spatial restriction.</param>
/// <param name="Projection">The schema fields a returned feature carries, or <c>null</c> for every field.</param>
/// <param name="Order">The requested sort keys, or <c>null</c> for the store's own order.</param>
/// <param name="Limit">The maximum number of features in the page, or <c>null</c> for no cap.</param>
/// <param name="Offset">The number of features to skip, or <c>null</c> to start at the first.</param>
/// <param name="Cursor">A store-issued continuation token, or <c>null</c> to start the plan.</param>
public sealed record FeatureQueryDto(
    IReadOnlyList<string>? Ids = null,
    PredicateDto? Where = null,
    BboxDto? Bbox = null,
    IReadOnlyList<string>? Projection = null,
    IReadOnlyList<OrderTermDto>? Order = null,
    int? Limit = null,
    int? Offset = null,
    string? Cursor = null)
{
    /// <summary>
    /// The wire of the unbounded plan, and the sugar of a request that sends
    /// no plan at all.
    /// </summary>
    public static FeatureQueryDto All { get; } = new();
}

/// <summary>
/// One requested sort key of a plan on the wire: the schema field name and the
/// direction, whose JSON values are the <see cref="SortDirection"/> names in
/// camelCase (<c>ascending</c>, <c>descending</c>).
/// </summary>
/// <param name="Field">The schema field to sort by.</param>
/// <param name="Direction">The direction to sort in.</param>
public sealed record OrderTermDto(string Field, SortDirection Direction = SortDirection.Ascending);

/// <summary>
/// One literal of a <see cref="PredicateDto"/> on the wire
/// (ADR-0158 §4): the literal kind, and the one member that kind carries.
/// An <c>integer</c> is carried as text so no precision is lost, a
/// <c>decimal</c> and a <c>dateTime</c> (epoch milliseconds) as a number, a
/// <c>boolean</c> as a flag, and a <c>string</c> or <c>null</c> as text or
/// nothing at all. A member the kind does not carry is
/// <c>invalid.arguments</c>.
/// </summary>
/// <param name="Kind">Which literal this is.</param>
/// <param name="Text">The text of a <see cref="LiteralKind.String"/> or <see cref="LiteralKind.Integer"/> literal.</param>
/// <param name="Number">The value of a <see cref="LiteralKind.Decimal"/> or <see cref="LiteralKind.DateTime"/> literal.</param>
/// <param name="Boolean">The value of a <see cref="LiteralKind.Boolean"/> literal.</param>
public sealed record LiteralDto(
    LiteralKind Kind,
    string? Text = null,
    double? Number = null,
    bool? Boolean = null);

/// <summary>
/// One node of a predicate tree on the wire (ADR-0158 §4), discriminated by
/// <see cref="Op"/>. The flat shape is deliberate: the core
/// <see cref="Predicate"/> is an abstract record with nested cases, and
/// serializing it directly puts a <c>$type</c> discriminator in the JSON that
/// the OpenAPI emitter and the TypeScript SDK generator can only model as
/// <c>unknown</c> — a discriminator no client should have to write.
///
/// <para>
/// A member the <c>op</c> does not carry is <c>invalid.arguments</c> rather
/// than ignored: a request is answered from what it says, and a filter sent as
/// <c>terms</c> on an <c>isNull</c> is a client that believes it filtered.
/// </para>
/// </summary>
/// <param name="Op">
/// The node kind: <c>and</c>, <c>or</c>, <c>compare</c>, <c>isNull</c>,
/// <c>isIn</c> or <c>constant</c>. There is no <c>not</c> node, because the
/// core vocabulary has none — negation is the <c>negated</c> flag on the two
/// tests that admit it.
/// </param>
/// <param name="Terms">The conjunctive (<c>and</c>) or disjunctive (<c>or</c>) terms.</param>
/// <param name="Field">The field a test reads.</param>
/// <param name="Operator">The comparison a <c>compare</c> makes.</param>
/// <param name="Value">The literal a <c>compare</c> tests against, or the truth a <c>constant</c> holds.</param>
/// <param name="Values">The literals an <c>isIn</c> tests membership of.</param>
/// <param name="Truth">The truth a <c>constant</c> holds.</param>
/// <param name="Negated">Whether an <c>isNull</c> or <c>isIn</c> is negated.</param>
public sealed record PredicateDto(
    string Op,
    IReadOnlyList<PredicateDto>? Terms = null,
    string? Field = null,
    string? Operator = null,
    LiteralDto? Value = null,
    IReadOnlyList<LiteralDto>? Values = null,
    bool? Truth = null,
    bool Negated = false);

/// <summary>
/// The JSON wire of a feature-read plan: the translation both ways between the
/// boundary DTOs above and the core plan and predicate tree
/// (ADR-0158 §4). Every failure is a typed <c>invalid.arguments</c> naming the
/// member, so a hand-written plan fails as a malformed plan rather than
/// selecting something the client did not ask for.
/// </summary>
public static class FeatureQueryWire
{
    /// <summary>
    /// The plan a wire request carries, with <paramref name="filter"/> and
    /// <paramref name="bbox"/> — the published sugar — folded in. The two
    /// spellings of one member must agree (ADR-0158 §2).
    /// </summary>
    /// <param name="plan">The plan spelling, or <c>null</c> when only sugar was sent.</param>
    /// <param name="filter">The published filter text, or <c>null</c>.</param>
    /// <param name="bbox">The published bounding box, or <c>null</c>.</param>
    public static FeatureQuery ToQuery(FeatureQueryDto? plan, string? filter, BboxDto? bbox)
    {
        var sugarWhere = FeatureFilter.Parse(filter);
        var sugarBox = bbox is null ? null : new BoundingBox(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY);
        if (plan is null)
        {
            return new FeatureQuery(Where: sugarWhere, BoundingBox: sugarBox);
        }

        var where = ToPredicate(plan.Where);
        var box = plan.Bbox is null ? null : new BoundingBox(plan.Bbox.MinX, plan.Bbox.MinY, plan.Bbox.MaxX, plan.Bbox.MaxY);
        Reject("filter", "where", sugarWhere, where, plan.Where is not null);
        Reject("bbox", "bbox", sugarBox, box, plan.Bbox is not null);
        return new FeatureQuery(
            plan.Ids is null ? null : [.. plan.Ids.Select(id => new FeatureId(id))],
            where ?? sugarWhere,
            box ?? sugarBox,
            plan.Projection,
            plan.Order?.Select(ToOrderTerm).ToArray(),
            plan.Limit,
            plan.Offset,
            plan.Cursor);
    }

    /// <summary>The wire of a plan, for a client that sends one (ADR-0158 §6).</summary>
    public static FeatureQueryDto ToDto(FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new FeatureQueryDto(
            query.Ids?.Select(id => id.Value).ToArray(),
            ToPredicateDto(query.Where),
            query.BoundingBox is null ? null : new BboxDto(
                query.BoundingBox.MinX, query.BoundingBox.MinY, query.BoundingBox.MaxX, query.BoundingBox.MaxY),
            query.Projection,
            query.Order?.Select(term => new OrderTermDto(term.Field, term.Direction)).ToArray(),
            query.Limit,
            query.Offset,
            query.Cursor);
    }

    /// <summary>
    /// The predicate tree a wire node describes, or <c>null</c> when there was
    /// no node. A node whose members do not match its <c>op</c>, an
    /// <c>op</c> outside the six, an empty <c>terms</c> or a missing field is
    /// <c>invalid.arguments</c> naming what was wrong.
    /// </summary>
    public static Predicate? ToPredicate(PredicateDto? dto)
    {
        if (dto is null)
        {
            return null;
        }

        var op = dto.Op?.Trim() ?? string.Empty;
        switch (op.ToLowerInvariant())
        {
            case "and" or "or":
                Every(dto, op);
                return TermsPredicate(dto, op);
            case "compare":
                return ComparePredicate(dto, op);
            case "isnull":
                return IsNullPredicate(dto, op);
            case "isin":
                return IsInPredicate(dto, op);
            case "constant":
                return ConstantPredicate(dto, op);
            default:
                throw Bad(
                    $"Unknown predicate op '{dto.Op}'; expected one of and, or, compare, isNull, isIn, constant.");
        }
    }

    private static Predicate TermsPredicate(PredicateDto dto, string op) =>
        op == "and"
            ? new Predicate.Every([.. Terms(dto, op)])
            : new Predicate.Some([.. Terms(dto, op)]);

    private static Predicate.Compare ComparePredicate(PredicateDto dto, string op)
    {
        Only(dto, op, "field", "operator", "value");
        return new Predicate.Compare(
            new FieldRef(Required(dto.Field, op, "field")),
            ToOperator(Required(dto.Operator, op, "operator")),
            ToLiteral(dto.Value, op));
    }

    private static Predicate.IsNull IsNullPredicate(PredicateDto dto, string op)
    {
        Only(dto, op, "field");
        return new Predicate.IsNull(new FieldRef(Required(dto.Field, op, "field")), dto.Negated);
    }

    private static Predicate.IsIn IsInPredicate(PredicateDto dto, string op)
    {
        Only(dto, op, "field", "values");
        return new Predicate.IsIn(
            new FieldRef(Required(dto.Field, op, "field")),
            dto.Values is null or { Count: 0 }
                ? throw Bad("A predicate 'isIn' needs a non-empty 'values'.")
                : [.. dto.Values.Select(value => ToLiteral(value, "isIn"))],
            dto.Negated);
    }

    private static Predicate.Constant ConstantPredicate(PredicateDto dto, string op)
    {
        Only(dto, op, "truth");
        if (dto.Truth is not { } truth)
        {
            throw Bad("A predicate 'constant' needs a boolean 'truth'.");
        }

        return new Predicate.Constant(truth);
    }

    /// <summary>The wire of a predicate tree, for a client that sends one.</summary>
    public static PredicateDto? ToPredicateDto(Predicate? predicate) => predicate switch
    {
        null => null,
        Predicate.Every every => CollectionDto("and", every.Terms),
        Predicate.Some some => CollectionDto("or", some.Terms),
        _ => TestDto(predicate),
    };

    private static PredicateDto CollectionDto(string op, IReadOnlyList<Predicate> terms) =>
        new(op, [.. terms.Select(term => ToPredicateDto(term)!)]);

    private static PredicateDto TestDto(Predicate predicate) => predicate switch
    {
        Predicate.Compare compare => new PredicateDto(
            "compare",
            Field: compare.Field.Name,
            Operator: Name(compare.Operator),
            Value: ToLiteralDto(compare.Value)),
        Predicate.IsNull isNull => new PredicateDto("isNull", Field: isNull.Field.Name, Negated: isNull.Negated),
        Predicate.IsIn isIn => new PredicateDto(
            "isIn",
            Field: isIn.Field.Name,
            Values: [.. isIn.Values.Select(value => ToLiteralDto(value))],
            Negated: isIn.Negated),
        Predicate.Constant constant => new PredicateDto("constant", Truth: constant.Value),
        _ => throw Bad($"Unsupported predicate node '{predicate.GetType().Name}'."),
    };

    private static OrderTerm ToOrderTerm(OrderTermDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (string.IsNullOrWhiteSpace(dto.Field))
        {
            throw Bad("An order term needs a field name.");
        }

        if (!Enum.IsDefined(dto.Direction))
        {
            throw Bad($"Unknown sort direction '{dto.Direction}'; expected {SortDirection.Ascending} or {SortDirection.Descending}.");
        }

        return new OrderTerm(dto.Field, dto.Direction);
    }

    private static Literal ToLiteral(LiteralDto? dto, string op)
    {
        if (dto is null)
        {
            throw Bad($"A predicate '{op}' needs a value literal.");
        }

        var literal = dto.Kind is LiteralKind.String or LiteralKind.Integer
            ? TextLiteral(dto, op)
            : OtherLiteral(dto, op);
        CheckLiteralExtras(dto);
        return literal;
    }

    private static Literal TextLiteral(LiteralDto dto, string op) => dto.Kind switch
    {
        LiteralKind.String => Literal.FromText(Present(dto, op, dto.Text, "text")),
        LiteralKind.Integer => Literal.FromInteger(Present(dto, op, dto.Text, "text")),
        _ => throw Bad($"Unknown literal kind '{dto.Kind}'."),
    };

    private static Literal OtherLiteral(LiteralDto dto, string op) => dto.Kind switch
    {
        LiteralKind.Decimal or LiteralKind.DateTime => NumberLiteral(dto, op),
        LiteralKind.Boolean or LiteralKind.Null => FlagLiteral(dto, op),
        _ => throw Bad($"Unknown literal kind '{dto.Kind}'."),
    };

    private static Literal NumberLiteral(LiteralDto dto, string op) => dto.Kind switch
    {
        LiteralKind.Decimal => Literal.FromNumber(Present(dto, op, dto.Number, "number")),
        LiteralKind.DateTime => Literal.FromMilliseconds((long)Present(dto, op, dto.Number, "number")),
        _ => throw Bad($"Unknown literal kind '{dto.Kind}'."),
    };

    private static Literal FlagLiteral(LiteralDto dto, string op)
    {
        if (dto.Kind == LiteralKind.Null)
        {
            return Literal.Null;
        }

        return dto.Boolean is { } flag
            ? Literal.FromBoolean(flag)
            : throw Bad($"A '{LiteralKind.Boolean}' literal needs a 'boolean'.");
    }

    private static void CheckLiteralExtras(LiteralDto dto)
    {
        Extra(dto.Kind, "text", dto.Text);
        Extra(dto.Kind, "number", dto.Number);
        Extra(dto.Kind, "boolean", dto.Boolean);
    }

    private static LiteralDto ToLiteralDto(Literal literal) => literal.Kind switch
    {
        LiteralKind.String or LiteralKind.Integer => TextLiteralDto(literal),
        LiteralKind.Decimal or LiteralKind.DateTime => NumberLiteralDto(literal),
        LiteralKind.Boolean => new LiteralDto(LiteralKind.Boolean, Boolean: literal.Boolean),
        _ => new LiteralDto(LiteralKind.Null),
    };

    private static LiteralDto TextLiteralDto(Literal literal) =>
        new(literal.Kind, Text: literal.Text);

    private static LiteralDto NumberLiteralDto(Literal literal) =>
        new(literal.Kind, Number: literal.Number);

    private static ComparisonOperator ToOperator(string name) =>
        Enum.TryParse<ComparisonOperator>(name, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw Bad(
                $"Unknown comparison operator '{name}'; expected one of "
                + $"{string.Join(", ", Enum.GetNames<ComparisonOperator>())}.");

    /// <summary>The camelCase name a client writes, which is the enum member's lower-camel spelling.</summary>
    private static string Name(ComparisonOperator value) =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString())!;

    private static IEnumerable<Predicate> Terms(PredicateDto dto, string op)
    {
        var terms = RequireTerms(dto, op);
        foreach (var term in terms)
        {
            yield return ToPredicate(term)
                ?? throw Bad($"A predicate '{op}' cannot carry a null term.");
        }
    }

    private static List<PredicateDto> RequireTerms(PredicateDto dto, string op)
    {
        if (dto.Terms is not { Count: > 0 } terms)
        {
            throw Bad($"A predicate '{op}' needs a non-empty 'terms'.");
        }

        return [.. terms];
    }

    /// <summary>
    /// Rejects a node that carries a member its <c>op</c> does not define, so
    /// no member of a plan is accepted and ignored.
    /// </summary>
    private static void Only(PredicateDto dto, string op, params string[] carried)
    {
        foreach (var (member, value) in new (string, object?)[]
                 {
                     ("terms", dto.Terms),
                     ("field", dto.Field),
                     ("operator", dto.Operator),
                     ("value", dto.Value),
                     ("values", dto.Values),
                     ("truth", dto.Truth),
                 })
        {
            if (!carried.Contains(member, StringComparer.Ordinal) && value is not null)
            {
                throw Bad($"A predicate '{op}' does not take '{member}'.");
            }
        }
    }

    private static void Every(PredicateDto dto, string op)
    {
        Only(dto, op, "terms");
        if (dto.Negated)
        {
            throw Bad($"A predicate '{op}' does not take 'negated'.");
        }
    }

    private static string Required(string? value, string op, string member) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Bad($"A predicate '{op}' needs a '{member}'.")
            : value;

    private static string Present(LiteralDto dto, string op, string? value, string member) =>
        value ?? throw Bad($"A predicate '{op}' literal of kind '{dto.Kind}' needs a '{member}'.");

    private static double Present(LiteralDto dto, string op, double? value, string member) =>
        value ?? throw Bad($"A predicate '{op}' literal of kind '{dto.Kind}' needs a '{member}'.");

    private static void Extra(LiteralKind kind, string member, object? value)
    {
        if (value is not null && !CarriesMember(kind, member))
        {
            throw Bad($"A literal of kind '{kind}' does not take '{member}'.");
        }
    }

    private static bool CarriesMember(LiteralKind kind, string member) => kind switch
    {
        LiteralKind.String or LiteralKind.Integer => member == "text",
        LiteralKind.Decimal or LiteralKind.DateTime => member == "number",
        LiteralKind.Boolean => member == "boolean",
        _ => true,
    };

    /// <summary>
    /// Folds the sugar onto the plan member it stands for: identical values are
    /// used once, and two different values for one member are refused by name
    /// (ADR-0158 §2) rather than one of them silently winning.
    /// </summary>
    private static void Reject<T>(string sugarMember, string planMember, T? sugar, T? plan, bool planSent)
        where T : notnull
    {
        if (ShouldReject(sugar is null, planSent, Equals(sugar, plan)))
        {
            RejectMismatch(sugarMember, planMember);
        }
    }

    private static bool ShouldReject(bool sugarMissing, bool planSent, bool equal) =>
        !sugarMissing && planSent && !equal;

    private static void RejectMismatch(string sugarMember, string planMember) =>
        throw Bad(
            $"The request sends both '{sugarMember}' (the sugar for the plan's '{planMember}') and "
            + $"'plan.{planMember}' with different values; send one spelling of each, or the same value twice.");

    private static SpatialException Bad(string message) => SpatialException.BadArguments($"The query plan is malformed: {message}");
}
