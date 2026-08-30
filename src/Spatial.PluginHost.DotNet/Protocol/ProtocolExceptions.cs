namespace Spatial.PluginHost.DotNet.Protocol;

/// <summary>A wire-protocol violation: a malformed envelope, an unsupported protocol version or a shape mismatch.</summary>
public class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message)
        : base(message)
    {
    }

    public WorkerProtocolException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A value that cannot cross the worker boundary in the inline protocol (ADR-0020/0025).</summary>
public class WorkerValueException : Exception
{
    public WorkerValueException(string message)
        : base(message)
    {
    }

    public WorkerValueException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The worker process (or channel) went away while a request was outstanding.</summary>
public sealed class WorkerDisconnectedException : WorkerProtocolException
{
    public WorkerDisconnectedException(string message)
        : base(message)
    {
    }

    public WorkerDisconnectedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
