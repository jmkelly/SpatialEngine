namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// A structured invocation error as a contract surface: a category
/// (<see cref="CapabilityErrorKind"/>), a stable dotted code and an
/// actionable message. <see cref="CapabilityError"/> implements it; Phase 4
/// job and stream events carry the same surface so consumers depend on the
/// abstraction, not on one error type.
/// </summary>
public interface ICapabilityError
{
    CapabilityErrorKind Kind { get; }

    string Code { get; }

    string Message { get; }
}
