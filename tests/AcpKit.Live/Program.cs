using System.Diagnostics;
using AcpKit;
using AcpKit.Protocol.V1;
using AcpKit.Live;

// Drives a real ACP agent binary over stdio with the generated client. This is the only tier
// that can prove the emitted shapes match what agents actually send, as opposed to what the
// schema says they should.
//
// It costs money: a prompt turn reaches a model. Every scenario is deliberately small, and the
// harness reports the tokens each one consumed.

var harness = Harness.FromEnvironment();
if (harness is null)
{
    Console.WriteLine("acpkit-live: no agent configured. Set ACPKIT_AGENT to the binary to drive.");
    Console.WriteLine("             Example: ACPKIT_AGENT=goose ACPKIT_AGENT_ARGS=acp");
    return 0;
}

Console.WriteLine($"acpkit-live: {harness.Describe()}");

var failures = 0;
foreach (var scenario in Scenarios.All)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var began = Stopwatch.GetTimestamp();

    try
    {
        var note = await scenario.Run(harness, timeout.Token);
        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        Console.WriteLine($"  ok    {scenario.Name}  ({seconds:F1}s){(note.Length > 0 ? "  " + note : string.Empty)}");
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"  FAIL  {scenario.Name}");
        Console.WriteLine($"          {e.GetType().Name}: {e.Message}");
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"  {Scenarios.All.Count}/{Scenarios.All.Count} passed"
    : $"  {Scenarios.All.Count - failures}/{Scenarios.All.Count} passed, {failures} failed");

return failures == 0 ? 0 : 1;

namespace AcpKit.Live
{
    internal sealed record Scenario(string Name, Func<Harness, CancellationToken, Task<string>> Run);

    /// <summary>How to launch the agent under test.</summary>
    internal sealed class Harness
    {
        private Harness(string command, string[] arguments)
        {
            Command = command;
            Arguments = arguments;
        }

        public string Command { get; }

        public string[] Arguments { get; }

        public static Harness? FromEnvironment()
        {
            var command = Environment.GetEnvironmentVariable("ACPKIT_AGENT");
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            var arguments = (Environment.GetEnvironmentVariable("ACPKIT_AGENT_ARGS") ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return new Harness(command, arguments);
        }

        public string Describe() => $"{Command} {string.Join(' ', Arguments)}".Trim();

        /// <summary>Start the agent and open a connection to it.</summary>
        public async Task<Session> ConnectAsync(CancellationToken cancellationToken)
        {
            var info = new ProcessStartInfo(Command)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            };

            foreach (var argument in Arguments)
            {
                info.ArgumentList.Add(argument);
            }

            var process = Process.Start(info)
                ?? throw new InvalidOperationException($"Could not start '{Command}'.");

            var diagnostics = new List<string>();
            var client = new RecordingClient();

            var connection = AgentConnection.Create(
                process.StandardOutput.BaseStream,
                process.StandardInput.BaseStream,
                client,
                diagnostics.Add);

            var pump = connection.RunAsync(cancellationToken);
            var session = new Session(process, connection, client, diagnostics, pump);

            // Agents that fail to start often say why on stderr and then exit silently.
            _ = Task.Run(async () =>
            {
                var text = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                if (text.Length > 0)
                {
                    lock (diagnostics)
                    {
                        diagnostics.Add(text.Trim());
                    }
                }
            }, CancellationToken.None);

            return session;
        }
    }

    /// <summary>A running agent process and the connection to it.</summary>
    internal sealed class Session(
        Process process,
        AgentConnection connection,
        RecordingClient client,
        List<string> diagnostics,
        Task pump) : IAsyncDisposable
    {
        public AgentConnection Connection => connection;

        public RecordingClient Client => client;

        public IReadOnlyList<string> Diagnostics
        {
            get
            {
                lock (diagnostics)
                {
                    return diagnostics.ToList();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            await connection.DisposeAsync();
            _ = pump;
            process.Dispose();
        }
    }

    /// <summary>A client that records what the agent told it and approves everything.</summary>
    internal sealed class RecordingClient : IAcpClient
    {
        private readonly List<string> _updateKinds = [];
        private readonly SemaphoreSlim _arrived = new(0);

        public IReadOnlyList<string> UpdateKinds
        {
            get
            {
                lock (_updateKinds)
                {
                    return _updateKinds.ToList();
                }
            }
        }

        public Task SessionUpdateAsync(SessionNotification request, CancellationToken cancellationToken)
        {
            lock (_updateKinds)
            {
                _updateKinds.Add(request.Update.Discriminator);
            }

            _arrived.Release();
            return Task.CompletedTask;
        }

        /// <summary>Wait for a particular update kind, giving up quietly after a deadline.</summary>
        public async Task WaitForUpdateAsync(string kind, TimeSpan within, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(within);

            try
            {
                while (!UpdateKinds.Contains(kind))
                {
                    await _arrived.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The caller decides whether the absence is a failure.
            }
        }

        public Task<RequestPermissionResponse> SessionRequestPermissionAsync(
            RequestPermissionRequest request,
            CancellationToken cancellationToken)
        {
            // Allow, so a turn that asks does not stall the run. Choosing the first offered
            // option rather than a hardcoded id keeps this working across agents.
            var option = request.Options.FirstOrDefault();
            return Task.FromResult(new RequestPermissionResponse
            {
                Outcome = option is null
                    ? new RequestPermissionOutcomeCancelled()
                    : new RequestPermissionOutcomeSelected
                    {
                        Value = new SelectedPermissionOutcome { OptionId = option.OptionId },
                    },
            });
        }

        public Task<ReadTextFileResponse> FsReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken) =>
            throw new AcpException(AcpErrorCode.MethodNotFound, "This client does not expose the file system.");

        public Task<WriteTextFileResponse> FsWriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken) =>
            throw new AcpException(AcpErrorCode.MethodNotFound, "This client does not expose the file system.");

        // v1 lets a client offer terminals to the agent. This one does not, and says so with
        // the code the protocol reserves for it rather than by failing in some other way.
        public Task<CreateTerminalResponse> TerminalCreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken) =>
            throw new AcpException(AcpErrorCode.MethodNotFound, "This client does not expose terminals.");

        public Task<TerminalOutputResponse> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken) =>
            throw new AcpException(AcpErrorCode.MethodNotFound, "This client does not expose terminals.");

        public Task<ReleaseTerminalResponse> TerminalReleaseAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken) =>
            throw new AcpException(AcpErrorCode.MethodNotFound, "This client does not expose terminals.");

        public Task<WaitForTerminalExitResponse> TerminalWaitForExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken) =>
            throw new AcpException(AcpErrorCode.MethodNotFound, "This client does not expose terminals.");

        public Task<KillTerminalResponse> TerminalKillAsync(KillTerminalRequest request, CancellationToken cancellationToken) =>
            throw new AcpException(AcpErrorCode.MethodNotFound, "This client does not expose terminals.");

    }
}
