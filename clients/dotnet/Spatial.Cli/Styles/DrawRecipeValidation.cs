using System.Text.RegularExpressions;

namespace Spatial.Cli;

/// <summary>
/// Validates the compact <see cref="DrawRecipe"/> before it is lowered to a
/// persisted MapLibre fragment. Catching a malformed colour or an
/// out-of-range number here keeps an unusable style out of the store: the
/// renderer otherwise rejects it only when a MapServer <c>export</c>/tile
/// request arrives.
/// </summary>
internal static partial class DrawRecipeValidation
{
    /// <summary>Rejects a colour that is not <c>#rrggbb</c> and numbers outside their documented ranges.</summary>
    public static void EnsureValid(DrawRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ValidateColour(recipe.Color);
        ValidateOpacity(recipe.Opacity);
        ValidatePositive(recipe.LineWidth, "Line width");
        ValidatePositive(recipe.Radius, "Radius");
    }

    private static void ValidateColour(string colour)
    {
        if (!ColourPattern().IsMatch(colour))
        {
            throw new CliUsageException($"Colour '{colour}' is invalid: expected #rrggbb.");
        }
    }

    private static void ValidateOpacity(double opacity)
    {
        if (opacity is not (>= 0 and <= 1))
        {
            throw new CliUsageException($"Opacity {opacity} is out of range: expected 0..1.");
        }
    }

    private static void ValidatePositive(double value, string name)
    {
        if (value is not > 0)
        {
            throw new CliUsageException($"{name} {value} is invalid: expected a positive number.");
        }
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColourPattern();
}
