using System.IO.Pipelines;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// An in-memory duplex pair for worker-channel tests: one side's output is
/// the other side's input, so two <c>WorkerChannel</c>s can talk without
/// spawning processes or files.
/// </summary>
public sealed class LoopbackPair : IDisposable
{
    private readonly Pipe _aToB = new();
    private readonly Pipe _bToA = new();

    /// <summary>Side A's incoming stream (delivers what side B wrote).</summary>
    public Stream AInput => _bToA.Reader.AsStream();

    /// <summary>Side A's outgoing stream (side B reads it).</summary>
    public Stream AOutput => _aToB.Writer.AsStream();

    /// <summary>Side B's incoming stream (delivers what side A wrote).</summary>
    public Stream BInput => _aToB.Reader.AsStream();

    /// <summary>Side B's outgoing stream (side A reads it).</summary>
    public Stream BOutput => _bToA.Writer.AsStream();

    /// <summary>Ends side A's input stream (side B writer completes): the clean-EOF disconnect case.</summary>
    public void CompleteAInput() => _bToA.Writer.Complete();

    public void Dispose()
    {
        _aToB.Reader.Complete();
        _aToB.Writer.Complete();
        _bToA.Reader.Complete();
        _bToA.Writer.Complete();
    }
}
