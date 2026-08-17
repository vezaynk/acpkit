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
        new("the negotiated version is not trusted over the payload shape", VersionIsAdvisory),
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
    /// The version in the response tells you less than it appears to.
    /// </summary>
    /// <remarks>
    /// goose echoes whatever <c>protocolVersion</c> it is sent — 1, 2, or 99 — while always
    /// replying with v1-shaped fields. A client that switched wire format on the returned
    /// number would therefore parse a v1 body as v2 and fail on the first field it could not
    /// find. What actually identifies the version is the shape of the body, so this asserts
    /// the number is present and otherwise treats it as advisory.
    /// </remarks>
    private static async Task<string> VersionIsAdvisory(Harness harness, CancellationToken ct)
    {
        await using var session = await harness.ConnectAsync(ct);

        var response = await session.Connection.InitializeAsync(
            new InitializeRequest
            {
                ProtocolVersion = new ProtocolVersion(1),
                ClientInfo = new Implementation { Name = "acpkit-live", Version = "0.1.0" },
            },
            ct);

        var negotiated = (ushort)response.ProtocolVersion.Value;
        if (negotiated > 1)
        {
            return $"negotiated {negotiated} but answered in v1 shapes — version is advisory";
        }

        return $"negotiated {negotiated}";
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
