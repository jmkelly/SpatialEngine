namespace Spatial.Spike.TileCache;

/// <summary>
/// The spike's knobs. The defaults are the realistic published map the bead
/// asks about: a five-layer city basemap over a metro extent, viewed at the
/// zoom a client actually sits at, with the per-tile feature counts a dense
/// city carries at that zoom.
/// </summary>
internal sealed record Options(
    int Zoom,
    int Layers,
    int Maps,
    int Density,
    int Iterations,
    int Warmup,
    int MaxEntries,
    long MaxBytes,
    string Label,
    string? Host)
{
    internal static Options Parse(string[] args)
    {
        var options = new Options(
            Zoom: 12,
            Layers: 5,
            Maps: 4,
            Density: 1,
            Iterations: 10,
            Warmup: 2,
            MaxEntries: 4096,
            MaxBytes: 64L * 1024 * 1024,
            Label: "default",
            Host: null);

        foreach (var arg in args)
        {
            var (name, value) = Split(arg);
            options = name switch
            {
                "--zoom" => options with { Zoom = Int(name, value) },
                "--layers" => options with { Layers = Int(name, value) },
                "--maps" => options with { Maps = Int(name, value) },
                "--density" => options with { Density = Int(name, value) },
                "--iterations" => options with { Iterations = Int(name, value) },
                "--warmup" => options with { Warmup = Int(name, value) },
                "--max-entries" => options with { MaxEntries = Int(name, value) },
                "--max-bytes" => options with { MaxBytes = Long(name, value) },
                "--label" => options with { Label = value },
                "--host" => options with { Host = value },
                _ => throw new ArgumentException($"Unknown option '{arg}'."),
            };
        }

        if (options.Zoom is < 0 or > 24)
        {
            throw new ArgumentException($"--zoom must be within [0, 24], got {options.Zoom}.");
        }

        if (options.Iterations < 1)
        {
            throw new ArgumentException($"--iterations must be positive, got {options.Iterations}.");
        }

        if (options.Layers < 2)
        {
            throw new ArgumentException($"--layers must be at least 2, got {options.Layers}.");
        }

        return options;
    }

    private static (string Name, string Value) Split(string arg)
    {
        var separator = arg.IndexOf('=', StringComparison.Ordinal);
        return separator < 0
            ? throw new ArgumentException($"'{arg}' needs a value, as --name=value.")
            : (arg[..separator], arg[(separator + 1)..]);
    }

    private static int Int(string name, string value) =>
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{name} must be a positive integer, got '{value}'.");

    private static long Long(string name, string value) =>
        long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"{name} must be a positive integer, got '{value}'.");
}
