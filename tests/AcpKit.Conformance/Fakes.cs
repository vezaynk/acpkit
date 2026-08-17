using AcpKit.Protocol.V2;

namespace AcpKit.Conformance;

/// <summary>
/// A minimal but honest ACP v2 agent.
/// </summary>
/// <remarks>
/// It follows the v2 prompt lifecycle rather than a convenient approximation of it:
/// <c>session/prompt</c> answers immediately with acceptance, and everything else — the user
/// message acknowledgement, the running state, the streamed reply, and the idle state carrying
/// the stop reason — arrives afterwards as notifications. A fake that returned the stop reason
/// from the prompt call would be testing v1 while claiming to test v2.
/// </remarks>
internal sealed class FakeAgent : IAcpAgent
{
    private ClientConnection? _client;
    private int _sessions;
    private TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// When set, a turn runs until cancelled rather than finishing on its own, so a scenario
    /// can observe what cancellation actually does to the state sequence.
    /// </summary>
    public bool HoldTurnUntilCancelled { get; set; }

    public void Attach(ClientConnection client) => _client = client;

    private ClientConnection Client => _client ?? throw new InvalidOperationException("Not attached.");

    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = new ProtocolVersion(2),
            Info = new Implementation { Name = "fake-agent", Version = "0.1.0" },
            Capabilities = new AgentCapabilities { Session = new SessionCapabilities() },
        });

    public Task<NewSessionResponse> SessionNewAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new NewSessionResponse
        {
            SessionId = new SessionId($"sess-{Interlocked.Increment(ref _sessions)}"),
        });

    public async Task<PromptResponse> SessionPromptAsync(PromptRequest request, CancellationToken cancellationToken)
    {
        // The turn runs after this method returns. Acceptance is all the response carries.
        _ = Task.Run(() => RunTurnAsync(request.SessionId), CancellationToken.None);
        await Task.Yield();
        return new PromptResponse();
    }

    private async Task RunTurnAsync(SessionId session)
    {
        var messageId = new MessageId("msg-1");

        await Notify(session, new SessionUpdateUserMessage
        {
            Value = new UserMessage { MessageId = messageId },
        });

        await Notify(session, new SessionUpdateStateUpdate
        {
            Value = new StateUpdateRunning { Value = new RunningStateUpdate() },
        });

        if (HoldTurnUntilCancelled)
        {
            // v2 is explicit about this: after session/cancel the agent flushes what it has and
            // then reports idle with stopReason "cancelled". Cancellation is not an error and
            // not a separate state — it is how the turn ends.
            await _cancelled.Task;

            await Notify(session, new SessionUpdateStateUpdate
            {
                Value = new StateUpdateIdle
                {
                    Value = new IdleStateUpdate { StopReason = Patch<StopReason>.Set(StopReason.Cancelled) },
                },
            });

            return;
        }

        await Notify(session, new SessionUpdateAgentMessageChunk
        {
            Value = new ContentChunk
            {
                MessageId = new MessageId("msg-2"),
                Content = new ContentBlockText { Value = new TextContent { Text = "hi" } },
            },
        });

        await Notify(session, new SessionUpdateStateUpdate
        {
            Value = new StateUpdateIdle
            {
                Value = new IdleStateUpdate { StopReason = Patch<StopReason>.Set(StopReason.EndTurn) },
            },
        });
    }

    private Task Notify(SessionId session, SessionUpdate update) =>
        Client.SessionUpdateAsync(new UpdateSessionNotification { SessionId = session, Update = update });

    /// <summary>Ask the client for permission, as an agent would before a sensitive action.</summary>
    public Task<RequestPermissionResponse> AskPermissionAsync(CancellationToken cancellationToken) =>
        Client.SessionRequestPermissionAsync(
            new RequestPermissionRequest
            {
                SessionId = new SessionId("sess-1"),
                Title = "Run this script?",
                Options =
                [
                    new PermissionOption
                    {
                        OptionId = new PermissionOptionId("allow-once"),
                        Name = "Allow once",
                        Kind = PermissionOptionKind.AllowOnce,
                    },
                ],
            },
            cancellationToken);

    /// <summary>
    /// Send a session update this protocol version does not define, by writing the envelope by
    /// hand. A generated agent cannot express an unknown variant, which is precisely the point.
    /// </summary>
    public Task SendRawUpdateAsync(string paramsJson, CancellationToken cancellationToken) =>
        Client.Peer.SendNotificationAsync(
            AcpMethods.SessionUpdate,
            System.Text.Json.JsonDocument.Parse(paramsJson).RootElement,
            AcpJsonContext.Default.JsonElement,
            cancellationToken);

    public Task<LoginAuthResponse> AuthLoginAsync(LoginAuthRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LoginAuthResponse());

    public Task<LogoutAuthResponse> AuthLogoutAsync(LogoutAuthRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LogoutAuthResponse());

    public Task<SetSessionConfigOptionResponse> SessionSetConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SetSessionConfigOptionResponse { ConfigOptions = [] });

    public Task SessionCancelAsync(CancelSessionNotification request, CancellationToken cancellationToken)
    {
        _cancelled.TrySetResult();
        return Task.CompletedTask;
    }

    public Task<ListSessionsResponse> SessionListAsync(ListSessionsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ListSessionsResponse { Sessions = [] });

    public Task<DeleteSessionResponse> SessionDeleteAsync(DeleteSessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new DeleteSessionResponse());

    public Task<ResumeSessionResponse> SessionResumeAsync(ResumeSessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ResumeSessionResponse());

    public Task<CloseSessionResponse> SessionCloseAsync(CloseSessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new CloseSessionResponse());
}

/// <summary>A client that records what it was told and answers permission prompts.</summary>
internal sealed class FakeClient : IAcpClient
{
    private readonly List<UpdateSessionNotification> _updates = [];
    private readonly SemaphoreSlim _arrived = new(0);

    public IReadOnlyList<UpdateSessionNotification> Updates
    {
        get
        {
            lock (_updates)
            {
                return _updates.ToList();
            }
        }
    }

    public bool PermissionAsked { get; private set; }

    public string PermissionOutcome { get; set; } = "allow-once";

    public Task<RequestPermissionResponse> SessionRequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        PermissionAsked = true;
        return Task.FromResult(new RequestPermissionResponse
        {
            Outcome = new RequestPermissionOutcomeSelected
            {
                Value = new SelectedPermissionOutcome
                {
                    OptionId = new PermissionOptionId(PermissionOutcome),
                },
            },
        });
    }

    public Task SessionUpdateAsync(UpdateSessionNotification request, CancellationToken cancellationToken)
    {
        lock (_updates)
        {
            _updates.Add(request);
        }

        _arrived.Release();
        return Task.CompletedTask;
    }

    /// <summary>Wait until at least <paramref name="count"/> updates have arrived.</summary>
    public async Task WaitForUpdatesAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_updates)
            {
                if (_updates.Count >= count)
                {
                    return;
                }
            }

            await _arrived.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
