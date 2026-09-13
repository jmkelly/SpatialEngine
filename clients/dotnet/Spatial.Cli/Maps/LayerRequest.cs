namespace Spatial.Cli;

/// <summary>
/// One parsed <c>--layer DATASET[=NAME]</c> request (ADR-0052): the dataset to
/// project and an optional display name override.
/// </summary>
internal sealed record LayerRequest(string Dataset, string? Name);
