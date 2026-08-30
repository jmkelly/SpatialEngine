using System.Runtime.InteropServices;
using System.Text;

namespace Spatial.PluginHost.DotNet.Protocol;

/// <summary>
/// A bounded, chunked line reader for the worker wire protocol: reads the next
/// <c>\n</c>-terminated UTF-8 line from a stream, carrying partial data between
/// calls so lines split across OS reads still arrive whole, and refuses lines
/// above <see cref="WorkerChannel.MaxLineBytes"/> (a host resource limit,
/// plan §19). One reader reads one stream for its whole lifetime.
/// </summary>
internal sealed class LineReader
{
    private readonly byte[] _chunk = new byte[8192];
    private readonly List<byte> _carry = new(256);
    private int _consumed;

    /// <summary>The next complete line, or null when the stream ends with no
    /// trailing delimiter (carried bytes are returned as one final line).</summary>
    public async ValueTask<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = FindNewline();
            if (newline >= 0)
            {
                return TakeLine(newline);
            }

            if (!await TryAppendChunkAsync(stream, maxBytes, cancellationToken))
            {
                return _carry.Count == 0 ? null : TakeDanglingLine();
            }
        }
    }

    /// <summary>Reads another chunk into the carry. False at clean EOF (nothing
    /// more to read); throws when the carry exceeds <paramref name="maxBytes"/>.</summary>
    private async ValueTask<bool> TryAppendChunkAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        if (_consumed == 0)
        {
            _consumed = await stream.ReadAsync(_chunk.AsMemory(), cancellationToken);
            if (_consumed == 0)
            {
                return false;
            }
        }

        _carry.AddRange(_chunk.AsSpan(0, _consumed));
        _consumed = 0;
        if (_carry.Count > maxBytes)
        {
            throw new WorkerProtocolException(
                $"a worker line exceeded the {maxBytes}-byte limit; the peer violated the protocol");
        }

        return true;
    }

    private int FindNewline()
    {
        for (var i = 0; i < _carry.Count; i++)
        {
            if (_carry[i] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private string TakeLine(int newline)
    {
        var line = PickLineSansCr(newline);
        _carry.RemoveRange(0, newline + 1);
        return line;
    }

    private string TakeDanglingLine()
    {
        var line = PickLineSansCr(_carry.Count);
        _carry.Clear();
        return line;
    }

    private string PickLineSansCr(int count)
    {
        var span = CollectionsMarshal.AsSpan(_carry)[..count];
        var end = span.Length > 0 && span[^1] == (byte)'\r' ? span.Length - 1 : span.Length;
        return Encoding.UTF8.GetString(span[..end]);
    }
}
