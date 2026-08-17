using System.Buffers;

namespace AcpKit;

/// <summary>
/// Reads newline-delimited JSON frames off a stream.
/// </summary>
/// <remarks>
/// <para>
/// Public because a peer is not the only thing that needs ACP's framing. A bridge, a proxy,
/// a recorder, or a debugger all have to split the same stream, and all of them want the
/// bytes rather than a decoded message — forwarding a frame verbatim cannot corrupt it or
/// break when the protocol grows a construct the forwarder has never seen.
/// </para>
/// <para>
/// Deliberately hand-rolled rather than layered on <c>StreamReader</c> or
/// <c>System.IO.Pipelines</c>: the former decodes eagerly and hides byte boundaries, and the
/// latter is a package dependency this assembly does not otherwise need. What is left is
/// small — find a <c>\n</c>, hand back everything before it.
/// </para>
/// <para>
/// Two properties matter for talking to real agents. There is no line-length ceiling: a
/// single <c>session/update</c> carrying a large diff or a base64 terminal snapshot can run
/// to megabytes, and a fixed buffer would truncate it into a parse error. And the reader
/// returns raw UTF-8 rather than a decoded string, because <c>Utf8JsonReader</c> wants bytes
/// and transcoding to UTF-16 and back is pure waste on the hot path.
/// </para>
/// </remarks>
public sealed class AcpFrameReader : IDisposable
{
    private const int InitialCapacity = 8 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer;
    private int _start;
    private int _length;
    private bool _disposed;

    /// <summary>Read frames from <paramref name="stream"/>.</summary>
    public AcpFrameReader(Stream stream)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
    }

    /// <summary>
    /// The next frame, without its terminator, or null at end of stream.
    /// </summary>
    /// <remarks>
    /// The returned memory points into an internal buffer and is valid only until the next
    /// call. Callers that keep it must copy.
    /// </remarks>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = FindNewline();
            if (newline >= 0)
            {
                var line = new ReadOnlyMemory<byte>(_buffer, _start, newline - _start);
                _length -= newline - _start + 1;
                _start = newline + 1;
                return Trim(line);
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                // End of stream. Anything buffered is a final line with no terminator, which
                // is legal NDJSON and is what a process that exits mid-write leaves behind.
                if (_length == 0)
                {
                    return null;
                }

                var tail = new ReadOnlyMemory<byte>(_buffer, _start, _length);
                _start += _length;
                _length = 0;
                return Trim(tail);
            }
        }
    }

    private int FindNewline()
    {
        var span = new ReadOnlySpan<byte>(_buffer, _start, _length);
        var index = span.IndexOf((byte)'\n');
        return index < 0 ? -1 : _start + index;
    }

    /// <summary>Strip a trailing CR, so CRLF-terminated streams behave identically.</summary>
    private static ReadOnlyMemory<byte> Trim(ReadOnlyMemory<byte> line) =>
        line.Length > 0 && line.Span[^1] == (byte)'\r' ? line[..^1] : line;

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        Compact();

        if (_start + _length == _buffer.Length)
        {
            Grow();
        }

        var read = await _stream
            .ReadAsync(_buffer.AsMemory(_start + _length), cancellationToken)
            .ConfigureAwait(false);

        if (read == 0)
        {
            return false;
        }

        _length += read;
        return true;
    }

    /// <summary>Slide unread bytes to the front so the tail of the buffer is usable again.</summary>
    private void Compact()
    {
        if (_start == 0)
        {
            return;
        }

        if (_length > 0)
        {
            Array.Copy(_buffer, _start, _buffer, 0, _length);
        }

        _start = 0;
    }

    private void Grow()
    {
        var larger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
        Array.Copy(_buffer, _start, larger, 0, _length);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
        _start = 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
