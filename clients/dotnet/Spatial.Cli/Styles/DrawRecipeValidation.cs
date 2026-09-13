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
        if (!ColourPattern().IsMatch(recipe.Color))
        {
            throw new CliUsageException($"Colour '{recipe.Color}' is invalid: expected #rrggbb.");
        }

        if (double.IsNaN(recipe.Opacity) || recipe.Opacity < 0 || recipe.Opacity > 1)
        {
            throw new CliUsageException($"Opacity {recipe.Opacity} is out of range: expected 0..1.");
        }

        if (double.IsNaN(recipe.LineWidth) || recipe.LineWidth <= 0)
        {
            throw new CliUsageException($"Line width {recipe.LineWidth} is invalid: expected a positive number.");
        }

        if (double.IsNaN(recipe.Radius) || recipe.Radius <= 0)
        {
            throw new CliUsageException($"Radius {recipe.Radius} is invalid: expected a positive number.");
        }
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColourPattern();
}
