namespace Spatial.Runtime.Capabilities;

/// <summary>
/// A provider or capability descriptor could not be registered: the provider
/// id is already registered, the descriptor violates a contract rule, or the
/// provider declared the same capability twice. Registration is host-wiring
/// programmer error, so it throws rather than returning a result.
/// </summary>
public sealed class CapabilityRegistrationException : Exception
{
    public CapabilityRegistrationException(string message)
        : base(message)
    {
    }

    public CapabilityRegistrationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
