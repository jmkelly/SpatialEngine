using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The facade's static service map, built once from
/// <see cref="GeoServicesOptions"/>. The catalog always advertises the
/// Geometry service plus every configured <c>FeatureServer</c>; invalid or
/// duplicate configuration fails fast at startup.
/// </summary>
public sealed class GeoServicesCatalog
{
    /// <summary>The fixed name of the served Geometry service.</summary>
    public const string GeometryServiceName = "Geometry";

    public GeoServicesCatalog(GeoServicesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Root = NormaliseRoot(options.Root);
        var services = new List<GeoServicesServiceEntry> { new(GeometryServiceName, string.Empty, "GeometryServer") };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GeometryServiceName };
        foreach (var service in options.Services)
        {
            services.Add(Validate(service, seen));
        }

        Services = services;
    }

    /// <summary>The normalised route prefix (no trailing slash, leading slash).</summary>
    public string Root { get; }

    /// <summary>Every served service in catalogue order.</summary>
    public IReadOnlyList<GeoServicesServiceEntry> Services { get; }

    /// <summary>Finds a configured service by name.</summary>
    public bool TryGet(string name, out GeoServicesServiceEntry entry)
    {
        foreach (var service in Services)
        {
            if (string.Equals(service.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                entry = service;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    private static GeoServicesServiceEntry Validate(GeoServicesServiceOptions service, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(service.Name) || string.IsNullOrWhiteSpace(service.Store))
        {
            throw new InvalidOperationException(
                "Every Spatial:GeoServices:Services entry needs a non-empty 'name' and 'store'.");
        }

        if (!seen.Add(service.Name))
        {
            throw new InvalidOperationException($"Duplicate GeoServices service name '{service.Name}'.");
        }

        if (!string.Equals(service.Type, "FeatureServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"GeoServices service '{service.Name}' has unsupported type '{service.Type}'; declared services are FeatureServer only " +
                "(MapServers are declared as Map publications under Spatial:Publications or created at runtime).");
        }

        return new GeoServicesServiceEntry(service.Name, service.Store, "FeatureServer");
    }

    private static string NormaliseRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Spatial:GeoServices:Root must be a non-empty URL prefix.");
        }

        var trimmed = root.Trim();
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.TrimEnd('/');
    }
}

/// <summary>One served logical service: its URL name, backing store and Esri type.</summary>
public sealed record GeoServicesServiceEntry(string Name, string Store, string Type);
