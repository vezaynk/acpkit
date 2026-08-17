using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace AcpKit.Conformance;

/// <summary>
/// A scenario: a name, and something to do that throws if the protocol misbehaved.
/// </summary>
internal sealed record Scenario(string Area, string Name, Func<CancellationToken, Task> Run);

/// <summary>
/// Runs scenarios and reports. Deliberately not a unit-test framework — these are
/// end-to-end exercises of a live connection, and the runner exists to sequence them and
/// give a non-zero exit code when one fails.
/// </summary>
internal sealed class Runner
{
    private readonly List<Scenario> _scenarios = [];

    public void Add(string area, string name, Func<CancellationToken, Task> run) =>
        _scenarios.Add(new Scenario(area, name, run));

    public async Task<int> RunAllAsync(TimeSpan perScenarioTimeout)
    {
        var failures = new List<(Scenario Scenario, Exception Error)>();
        var stopwatch = Stopwatch.StartNew();
        string? area = null;

        foreach (var scenario in _scenarios)
        {
            if (scenario.Area != area)
            {
                area = scenario.Area;
                Console.WriteLine();
                Console.WriteLine($"  {area}");
            }

            using var timeout = new CancellationTokenSource(perScenarioTimeout);
            var began = Stopwatch.GetTimestamp();
            try
            {
                await scenario.Run(timeout.Token).ConfigureAwait(false);
                var ms = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                Console.WriteLine($"    ok    {scenario.Name}  ({ms:F0} ms)");
            }
            catch (Exception e)
            {
                var reason = e is OperationCanceledException && timeout.IsCancellationRequested
                    ? new TimeoutException($"Timed out after {perScenarioTimeout.TotalSeconds:F0}s.")
                    : e;
                failures.Add((scenario, reason));
                Console.WriteLine($"    FAIL  {scenario.Name}");
            }
        }

        Console.WriteLine();
        foreach (var (scenario, error) in failures)
        {
            Console.WriteLine($"  FAILED: {scenario.Area} / {scenario.Name}");
            Console.WriteLine($"    {error.GetType().Name}: {error.Message}");
            if (error.StackTrace is { } trace)
            {
                foreach (var line in trace.Split('\n').Take(4))
                {
                    Console.WriteLine($"    {line.Trim()}");
                }
            }

            Console.WriteLine();
        }

        var passed = _scenarios.Count - failures.Count;
        Console.WriteLine($"  {passed}/{_scenarios.Count} passed in {stopwatch.Elapsed.TotalSeconds:F1}s");
        return failures.Count == 0 ? 0 : 1;
    }
}

/// <summary>Assertions that read as statements about the protocol, not about objects.</summary>
internal static class Expect
{
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{what}: expected <{expected}>, got <{actual}>.");
        }
    }

    public static void True(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException(what);
        }
    }

    public static void Contains(string needle, string haystack, string what)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{what}: expected to find <{needle}> in <{haystack}>.");
        }
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action, string what)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException($"{what}: expected {typeof(T).Name}, got {other.GetType().Name}: {other.Message}");
        }

        throw new InvalidOperationException($"{what}: expected {typeof(T).Name}, but nothing was thrown.");
    }
}

/// <summary>
/// An in-memory stream carrying bytes one way. Two of them make a duplex link, which is how
/// a scenario connects two <see cref="AcpPeer"/> instances without spawning a process.
/// </summary>
internal sealed class LoopbackStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private ReadOnlyMemory<byte> _remainder;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Signal end-of-stream to the reader, as a closing process would.</summary>
    public void Finish() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remainder.IsEmpty)
        {
            try
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            if (!_chunks.Reader.TryRead(out var chunk))
            {
                return 0;
            }

            _remainder = chunk;
        }

        var take = Math.Min(buffer.Length, _remainder.Length);
        _remainder[..take].CopyTo(buffer);
        _remainder = _remainder[take..];
        return take;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _chunks.Writer.TryWrite(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Write raw text, for scenarios that need to speak to a peer by hand.</summary>
    public void WriteRaw(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>A pair of peers wired to each other, plus the raw wire for hand-written traffic.</summary>
internal sealed class Link : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();

    private Link(LoopbackStream leftToRight, LoopbackStream rightToLeft)
    {
        LeftToRight = leftToRight;
        RightToLeft = rightToLeft;
    }

    public LoopbackStream LeftToRight { get; }

    public LoopbackStream RightToLeft { get; }

    public AcpPeer Left { get; private set; } = null!;

    public AcpPeer Right { get; private set; } = null!;

    public Task LeftPump { get; private set; } = Task.CompletedTask;

    public Task RightPump { get; private set; } = Task.CompletedTask;

    public static Link Create(AcpPeerOptions? left = null, AcpPeerOptions? right = null)
    {
        var leftToRight = new LoopbackStream();
        var rightToLeft = new LoopbackStream();
        var link = new Link(leftToRight, rightToLeft)
        {
            Left = new AcpPeer(rightToLeft, leftToRight, left),
            Right = new AcpPeer(leftToRight, rightToLeft, right),
        };

        link.LeftPump = link.Left.RunAsync(link._shutdown.Token);
        link.RightPump = link.Right.RunAsync(link._shutdown.Token);
        return link;
    }

    /// <summary>A peer whose far side is driven by hand, one raw line at a time.</summary>
    public static Link CreateHalf(AcpPeerOptions? left = null)
    {
        var leftToRight = new LoopbackStream();
        var rightToLeft = new LoopbackStream();
        var link = new Link(leftToRight, rightToLeft)
        {
            Left = new AcpPeer(rightToLeft, leftToRight, left),
        };

        link.LeftPump = link.Left.RunAsync(link._shutdown.Token);
        return link;
    }

    /// <summary>The next line the left peer wrote, as text.</summary>
    public async Task<string> ReadFromLeftAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var written = 0;
        while (true)
        {
            var read = await LeftToRight.ReadAsync(buffer.AsMemory(written), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            written += read;
            var text = Encoding.UTF8.GetString(buffer, 0, written);
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            if (newline >= 0)
            {
                return text[..newline];
            }
        }

        throw new InvalidOperationException("The peer wrote nothing before closing.");
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        LeftToRight.Finish();
        RightToLeft.Finish();
        await Left.DisposeAsync().ConfigureAwait(false);
        if (Right is not null)
        {
            await Right.DisposeAsync().ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }
}
