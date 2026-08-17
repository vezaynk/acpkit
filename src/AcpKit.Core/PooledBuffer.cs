using System.Buffers;

namespace AcpKit;

/// <summary>
/// A growable <see cref="IBufferWriter{T}"/> over pooled memory, used to build one outbound
/// message before it is handed to the stream as a single write.
/// </summary>
/// <remarks>
/// <see cref="ArrayBufferWriter{T}"/> would do the same job, but it allocates a fresh array
/// per message and every ACP conversation is a long stream of small ones. Renting keeps the
/// steady-state allocation at roughly zero.
/// </remarks>
internal sealed class PooledBuffer : IBufferWriter<byte>, IDisposable
{
    private const int InitialCapacity = 1024;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialCapacity);
    private int _written;
    private bool _disposed;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_written++] = value;
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint <= 0)
        {
            sizeHint = 1;
        }

        if (_buffer.Length - _written >= sizeHint)
        {
            return;
        }

        var capacity = Math.Max(_buffer.Length * 2, _written + sizeHint);
        var larger = ArrayPool<byte>.Shared.Rent(capacity);
        Array.Copy(_buffer, larger, _written);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

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
