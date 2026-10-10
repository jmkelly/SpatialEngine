namespace Spatial.StoreConnections;

/// <summary>
/// Host configuration for the runtime store-connection registry:
/// <c>Spatial:StoreConnections:Path</c> is the protected JSON file holding
/// one connection string per store key, relative to the host's working
/// directory. Missing file means no runtime entries: the stores run on
/// startup configuration until the admin sets one.
/// </summary>
public sealed class StoreConnectionOptions
{
    /// <summary>The runtime connection file; relative to the host's working directory.</summary>
    public string Path { get; set; } = "./data/store-connections.json";
}
