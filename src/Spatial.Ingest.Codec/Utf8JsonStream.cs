using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Spatial.Ingest.Codec;

/// <summary>
/// A pull-based <see cref="Utf8JsonReader"/> over a stream: a fixed buffer that
/// is compacted and refilled as the reader consumes it, growing only when a
/// single JSON value does not fit. <see cref="JsonDocument.Parse(Stream)"/>
/// is the reason ingest was not streaming — it materialises the whole document
/// before the first feature exists — and this is the minimum machinery needed
/// to read a <c>FeatureCollection</c>'s array one feature at a time.
/// </summary>
internal sealed class Utf8JsonStream : IDisposable
{
    private const int WorkingSize = 64 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer;
    private int _length;
    private int _consumed;
    private bool _final;
    private bool _finished;
    private JsonReaderState _state;
    private JsonTokenType _tokenType;
    private string? _valueText;

    public Utf8JsonStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(WorkingSize);
    }

    /// <summary>Whether the reader has reached the end of the document.</summary>
    public bool Exhausted => _finished;

    /// <summary>The kind of the token most recently read.</summary>
    public JsonTokenType TokenType => _tokenType;

    /// <summary>The scalar value of the most recently read token, unescaped.</summary>
    public string? ValueText => _valueText;

    /// <summary>Whether the stream ended on a well-formed document boundary.</summary>
    public bool IsComplete => _finished && _consumed >= _length;

    /// <summary>
    /// Reads the next token, refilling the buffer as needed. Returns
    /// <c>false</c> at end of document.
    /// </summary>
    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
    {
        while (!_finished)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_consumed < _length)
            {
                // A non-final block that runs out mid-token returns false with
                // the position unadvanced, so refilling and re-parsing from the
                // same offset is exactly the right recovery.
                // Exactly the valid bytes: a span reaching to the end of the
                // rented array would let a final block read whatever the pool
                // left there.
                var reader = new Utf8JsonReader(_buffer.AsSpan(_consumed, _length - _consumed), _final, state: _state);
                if (reader.Read())
                {
                    _state = reader.CurrentState;
                    _consumed += (int)reader.BytesConsumed;
                    _tokenType = reader.TokenType;
                    _valueText = reader.ValueIsEscaped
                        ? reader.GetString()
                        : Encoding.UTF8.GetString(reader.ValueSpan);
                    return true;
                }
            }

            if (_final)
            {
                _finished = true;
                return false;
            }

            await RefillAsync(cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Parses the value the reader is positioned on into a document, requiring
    /// the whole value to be contiguous in the buffer. Grows the buffer if one
    /// value is larger than the working size, so a single enormous feature costs
    /// more memory than usual rather than corrupting the stream. The caller
    /// owns the returned document.
    /// </summary>
    public async ValueTask<JsonDocument> ReadValueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_consumed < _length && ValueExtent() is { Complete: true } extent)
            {
                return Parse(extent);
            }

            if (_final)
            {
                throw new IngestFormatException("The document ended in the middle of a JSON value.");
            }

            // The value is larger than the buffer, which is ordinary for one
            // detailed polygon. Refilling compacts, tops up and grows, so the
            // next pass has more of it.
            await RefillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private JsonDocument Parse(Extent extent)
    {
        try
        {
            // The value gets its own exactly-sized final block and a fresh
            // reader: it is a self-contained document, and inheriting the
            // stream's state would leave the outer container's depth open.
            // ParseValue insists on reaching end-of-data, so an unbounded span
            // would read into the next token and call it trailing content.
            var reader = new Utf8JsonReader(_buffer.AsSpan(_consumed + extent.Start, extent.Length), true, state: default);
            var document = JsonDocument.ParseValue(ref reader);
            _state = extent.State;
            _consumed += extent.Start + extent.Length;
            _tokenType = JsonTokenType.None;
            _valueText = null;
            return document;
        }
        catch (JsonException exception)
        {
            throw new IngestFormatException($"The document is not valid JSON: {exception.Message}", exception);
        }
    }

    /// <summary>One JSON value's position, size and the reader state after it.</summary>
    private readonly record struct Extent(int Start, int Length, JsonReaderState State, bool Complete);

    /// <summary>
    /// Walks the value starting at the current position, or reports that the
    /// buffer does not hold all of it yet.
    /// <para>
    /// The value does not start at the current position: a separator and
    /// whitespace may sit between them, and only the reader may consume those,
    /// because its state records that a value has just been read. Skipping them
    /// by hand leaves it expecting the separator it never saw. A
    /// sequence-backed reader reports where the token it read actually started,
    /// which is the value's first byte.
    /// </para>
    /// </summary>
    private Extent? ValueExtent()
    {
        var sequence = new ReadOnlySequence<byte>(_buffer.AsMemory(_consumed, _length - _consumed));
        var reader = new Utf8JsonReader(sequence, false, state: _state);
        if (reader.Read() is false)
        {
            return null;
        }

        var start = (int)reader.TokenStartIndex;
        if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return new Extent(start, (int)reader.BytesConsumed - start, reader.CurrentState, true);
        }

        var depth = 1;
        while (depth > 0)
        {
            if (reader.Read() is false)
            {
                return null;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                depth++;
            }
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                depth--;
            }
        }

        return new Extent(start, (int)reader.BytesConsumed - start, reader.CurrentState, true);
    }

    /// <summary>
    /// The kind of the next token, without consuming it. Needed where a token
    /// decides between structure and value: reading it and rewinding would put
    /// the position on the separator before an array element, and the value
    /// that follows would be parsed as if the separator were part of it.
    /// </summary>
    public async ValueTask<JsonTokenType> PeekAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_consumed < _length)
            {
                // Exactly the valid bytes: a span reaching to the end of the
                // rented array would let a final block read whatever the pool
                // left there.
                var reader = new Utf8JsonReader(_buffer.AsSpan(_consumed, _length - _consumed), _final, state: _state);
                if (reader.Read())
                {
                    return reader.TokenType;
                }
            }

            if (_final)
            {
                return JsonTokenType.None;
            }

            await RefillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);

    /// <summary>Moves the unconsumed tail to the front of the buffer.</summary>
    private void Compact()
    {
        if (_consumed == 0)
        {
            return;
        }

        Array.Copy(_buffer, _consumed, _buffer, 0, _length - _consumed);
        _length -= _consumed;
        _consumed = 0;
    }

    private void Grow()
    {
        var next = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
        Array.Copy(_buffer, next, _length);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    /// <summary>Compacts, then tops the buffer up from the stream.</summary>
    private async ValueTask RefillAsync(CancellationToken cancellationToken)
    {
        Compact();
        if (_length == _buffer.Length)
        {
            Grow();
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_length), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            _final = true;
            return;
        }

        _length += read;
    }
}
