namespace Spatial.Cli;

/// <summary>
/// The compact, human/LLM-authored draw recipe (ADR-0052): the same fields
/// the seed manifest and the workbench composer expose. It lowers to the
/// persisted MapLibre fragment (ADR-0047) and back.
/// </summary>
public sealed record DrawRecipe(
    string Color = "#4fc3f7",
    double Opacity = 1,
    double LineWidth = 2,
    double Radius = 5,
    bool Visible = true);
