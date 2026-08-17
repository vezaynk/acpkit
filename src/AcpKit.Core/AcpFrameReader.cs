using System.Buffers;
using System.IO.Pipelines;

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
/// Built on <see cref="PipeReader"/>, which owns the buffering. That matters more than the
/// lines it saves: a frame is handed back as a slice of the pipe's own memory whenever it
/// arrived contiguously, so the common case copies nothing, and the awkward cases — a frame
/// split across reads, or one larger than any single buffer — become the pipe's problem
/// rather than hand-written index arithmetic.
/// </para>
/// <para>
/// There is deliberately no ceiling on frame size. A single <c>session/update</c> carrying a
/// diff or a base64 terminal snapshot runs to megabytes, and a reader that capped its buffer
/// would turn a legitimate message into a parse error. Frames are raw UTF-8 rather than
/// decoded strings, because <c>Utf8JsonReader</c> wants bytes and transcoding to UTF-16 and
/// back is pure waste on the hot path.
/// </para>
/// </remarks>
public sealed class AcpFrameReader : IDisposable
{
    private readonly PipeReader _reader;
    private readonly bool _ownsReader;

    private SequencePosition? _unconsumed;
    private byte[]? _joined;
    private bool _disposed;

    /// <summary>Read frames from <paramref name="stream"/>.</summary>
    public AcpFrameReader(Stream stream)
        : this(PipeReader.Create(stream ?? throw new ArgumentNullException(nameof(stream))), ownsReader: true)
    {
    }

    /// <summary>
    /// Read frames from an existing <see cref="PipeReader"/>, which the caller continues to
    /// own. Use this to sit on a pipeline something else already established.
    /// </summary>
    public AcpFrameReader(PipeReader reader)
        : this(reader ?? throw new ArgumentNullException(nameof(reader)), ownsReader: false)
    {
    }

    private AcpFrameReader(PipeReader reader, bool ownsReader)
    {
        _reader = reader;
        _ownsReader = ownsReader;
    }

    /// <summary>
    /// The next frame, without its terminator, or null at end of stream.
    /// </summary>
    /// <remarks>
    /// The returned memory is valid only until the next call: it usually points into the
    /// pipe's buffer, which is released as soon as reading resumes. Callers keeping a frame
    /// must copy it.
    /// </remarks>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            // Releasing the previous frame happens here rather than before returning it, which
            // is what lets a caller borrow the pipe's memory instead of being handed a copy.
            if (_unconsumed is { } position)
            {
                _reader.AdvanceTo(position);
                _unconsumed = null;
            }

            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.PositionOf((byte)'\n') is { } newline)
            {
                var frame = buffer.Slice(0, newline);
                _unconsumed = buffer.GetPosition(1, newline);
                return Trim(Contiguous(frame));
            }

            if (result.IsCompleted)
            {
                if (buffer.IsEmpty)
                {
                    _reader.AdvanceTo(buffer.Start);
                    return null;
                }

                // A final frame with no terminator: legal NDJSON, and what a process that
                // exits mid-write leaves behind.
                _unconsumed = buffer.End;
                return Trim(Contiguous(buffer));
            }

            // Nothing complete yet. Consuming nothing while marking everything examined is
            // what tells the pipe to wait for more instead of handing back the same bytes.
            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// A frame as one span of memory, borrowing the pipe's buffer when it already is one.
    /// </summary>
    private ReadOnlyMemory<byte> Contiguous(ReadOnlySequence<byte> frame)
    {
        if (frame.IsSingleSegment)
        {
            return frame.First;
        }

        // Split across pipe segments, so it has to be joined. The scratch array is reused and
        // only grows, which keeps a stream of large frames from churning allocations.
        var length = checked((int)frame.Length);
        if (_joined is null || _joined.Length < length)
        {
            _joined = new byte[Math.Max(length, (_joined?.Length ?? 4096) * 2)];
        }

        frame.CopyTo(_joined);
        return _joined.AsMemory(0, length);
    }

    /// <summary>Strip a trailing CR, so CRLF-terminated streams behave identically.</summary>
    private static ReadOnlyMemory<byte> Trim(ReadOnlyMemory<byte> frame) =>
        frame.Length > 0 && frame.Span[^1] == (byte)'\r' ? frame[..^1] : frame;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _joined = null;

        if (_ownsReader)
        {
            _reader.Complete();
        }
    }
}
