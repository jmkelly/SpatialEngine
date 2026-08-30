namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// Thrown when a worker cannot be activated: the package is invalid, the
/// provider id is already registered (drain it first), or the worker never
/// completed its handshake inside the startup timeout (its stderr is included
/// when available).
/// </summary>
public sealed class WorkerActivationException : Exception
{
    public WorkerActivationException(string message)
        : base(message)
    {
    }

    public WorkerActivationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
