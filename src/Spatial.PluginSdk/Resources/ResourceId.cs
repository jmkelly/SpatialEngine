namespace Spatial.PluginSdk.Resources;

/// <summary>
/// The opaque identity of a runtime-owned resource (plan §8 "opaque
/// handles"): minted by the runtime when a resource is created, never reused
/// and meaningless to clients on its own — the runtime resolves the id back
/// to the registered resource before anything can be leased or read (the
/// client cannot forge a resource by inventing an id).
/// </summary>
public readonly record struct ResourceId(Guid Value)
{
    /// <summary>Mints a fresh opaque resource id. Ids are never reused.</summary>
    public static ResourceId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
