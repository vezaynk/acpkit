using AcpKit.Protocol.V1;

namespace AcpKit.Live;

/// <summary>
/// What we ask of a real agent.
/// </summary>
/// <remarks>
/// Ordered cheapest first. The handshake and session scenarios reach no model at all, so a
/// misconfigured harness fails before anything is billed; only the prompt scenarios spend.
/// </remarks>
internal static class Scenarios
{
    public static IReadOnlyList<Scenario> All =>
    [
        new("initialize returns capabilities this client can read", Initialize),
        new("the line is detected from the payload shape, not the version", ShapeBeatsVersion),
        new("a session opens", OpenSession),
        new("a prompt turn completes with a stop reason", PromptTurn),
        new("cancelling a turn is accepted", CancelTurn),
    ];

    private static InitializeRequest Handshake => new()
    {
        ProtocolVersion = new ProtocolVersion(1),
        ClientInfo = new Implementation { Name = "acpkit-live", Version = "0.1.0" },
        ClientCapabilities = new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
            Terminal = false,
        },
    };

    private static async Task<string> Initialize(Harness harness, CancellationToken ct)
    {
        await using var session = await harness.ConnectAsync(ct);
        var response = await session.Connection.InitializeAsync(Handshake, ct);

        var agent = response.AgentInfo is { } info ? $"{info.Name} {info.Version}" : "(unnamed)";
        var loadSession = response.AgentCapabilities?.LoadSession == true;
        var http = response.AgentCapabilities?.McpCapabilities?.Http == true;
        var auth = response.AuthMethods?.Length ?? 0;

        return $"{agent}, loadSession={loadSession}, mcp.http={http}, authMethods={auth}";
    }

    /// <summary>
    /// Which line the agent speaks is read off the response shape, not the version it reports.
    /// </summary>
    /// <remarks>
    /// goose answers whatever <c>protocolVersion</c> it is sent while always replying in v1
    /// field names, so this asks for 2 on purpose. A client that believed the number would
    /// build a v2 connection and fail on the first field it could not find; AcpHandshake reads
    /// the field names instead and gets it right.
    /// </remarks>
    private static async Task<string> ShapeBeatsVersion(Harness harness, CancellationToken ct)
    {
        await using var session = await harness.ConnectAsync(ct);

        var parameters = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new InitializeRequest
            {
                // Deliberately optimistic: ask for v2 and see what comes back.
                ProtocolVersion = new ProtocolVersion(2),
                ClientInfo = new Implementation { Name = "acpkit-live", Version = "0.1.0" },
            },
            AcpJsonContext.Default.InitializeRequest);

        using var result = await session.Connection.Peer.SendRawRequestAsync(
            AcpHandshake.InitializeMethod, parameters, ct);

        var declared = AcpHandshake.DeclaredVersion(result.RootElement);
        var shape = AcpHandshake.DetectShape(result.RootElement);

        if (shape == AcpProtocolShape.Unknown)
        {
            throw new InvalidOperationException(
                "Could not tell which line the agent speaks from its initialize response.");
        }

        var disagrees = AcpHandshake.VersionDisagreesWithShape(result.RootElement);
        var note = $"declared={declared}, shape={shape}";

        return disagrees
            ? $"{note} — reported a version it does not speak, so the shape is what to trust"
            : note;
    }

    private static async Task<string> OpenSession(Harness harness, CancellationToken ct)
    {
        await using var session = await harness.ConnectAsync(ct);
        await session.Connection.InitializeAsync(Handshake, ct);

        var opened = await session.Connection.SessionNewAsync(
            new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [] },
            ct);

        if (opened.SessionId.Value.Length == 0)
        {
            throw new InvalidOperationException("The agent returned an empty session id.");
        }

        return $"sessionId={opened.SessionId.Value}";
    }

    private static async Task<string> PromptTurn(Harness harness, CancellationToken ct)
    {
        await using var session = await harness.ConnectAsync(ct);
        await session.Connection.InitializeAsync(Handshake, ct);

        var opened = await session.Connection.SessionNewAsync(
            new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [] },
            ct);

        var response = await session.Connection.SessionPromptAsync(
            new PromptRequest
            {
                SessionId = opened.SessionId,
                Prompt =
                [
                    new ContentBlockText
                    {
                        Value = new TextContent { Text = "Reply with exactly the word OK. Do not use any tools." },
                    },
                ],
            },
            ct);

        // The prompt response and the update notifications race: notifications are delivered on
        // their own ordered pump, so the reply can return while the last chunks are still in
        // flight. Give them a moment to land before judging what arrived.
        await session.Client.WaitForUpdateAsync("agent_message_chunk", TimeSpan.FromSeconds(10), ct);

        var kinds = string.Join(",", session.Client.UpdateKinds.Distinct().Order(StringComparer.Ordinal));
        if (!session.Client.UpdateKinds.Contains("agent_message_chunk"))
        {
            throw new InvalidOperationException(
                $"The turn ended with {response.StopReason.Value} but streamed no agent_message_chunk. Saw [{kinds}].");
        }

        return $"stopReason={response.StopReason.Value}, updates=[{kinds}]";
    }

    /// <summary>
    /// A cancel that arrives after the turn has finished is still legal, so this asserts the
    /// notification is accepted rather than asserting on a particular stop reason.
    /// </summary>
    private static async Task<string> CancelTurn(Harness harness, CancellationToken ct)
    {
        await using var session = await harness.ConnectAsync(ct);
        await session.Connection.InitializeAsync(Handshake, ct);

        var opened = await session.Connection.SessionNewAsync(
            new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [] },
            ct);

        await session.Connection.SessionCancelAsync(
            new CancelNotification { SessionId = opened.SessionId },
            ct);

        return "cancel accepted";
    }
}
