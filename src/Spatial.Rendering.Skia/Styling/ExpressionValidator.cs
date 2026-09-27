using Spatial.Contracts;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// The compile-time pass over a compiled expression: a <c>var</c> must be
/// bound by an enclosing <c>let</c>. An unbound binding is a typed
/// <c>invalid.arguments</c> at compile time rather than a missing value at
/// draw time, which is the rejection discipline the rest of the compiler
/// keeps.
/// </summary>
internal static class ExpressionValidator
{
    public static void Validate(StyleExpression expression) => Visit(expression, []);

    private static void Visit(StyleExpression expression, IReadOnlyCollection<string> bound)
    {
        switch (expression)
        {
            case VariableExpression variable when !bound.Contains(variable.Name):
                throw SpatialException.BadArguments(
                    $"Expression ['var','{variable.Name}'] has no matching 'let' binding.");
            case LetExpression let:
                // A let's bindings and its result share the bindings' scope, so
                // the generic child walk would judge the result without them.
                Visit(let.Bindings.Select(binding => binding.Value), bound);
                Visit(let.Result, [.. bound, .. let.Bindings.Select(binding => binding.Key)]);
                return;
            default:
                break;
        }

        Visit(expression.Children, bound);
    }

    private static void Visit(IEnumerable<StyleExpression> expressions, IReadOnlyCollection<string> bound)
    {
        foreach (var child in expressions)
        {
            Visit(child, bound);
        }
    }
}
