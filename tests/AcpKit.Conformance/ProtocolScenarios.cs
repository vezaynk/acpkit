using System.Text.Json;
using AcpKit.Protocol.V2;

namespace AcpKit.Conformance;

/// <summary>
/// A generated client driving a generated agent over a real transport.
/// </summary>
/// <remarks>
/// Nothing here is a mock. Both sides are the emitted <c>Connection</c> types, talking
/// newline-delimited JSON-RPC over an in-memory duplex link, with the real converters and the
/// source-generated contracts in the path. What it proves is what no compile check can: that
/// the shapes round-trip, that dispatch reaches the right handler, and that the v2 prompt
/// lifecycle behaves as the specification describes.
/// </remarks>
internal static class ProtocolScenarios
{
    private const string Area = "protocol v2";

    public static void Register(Runner runner)
    {
        runner.Add(Area, "initialize negotiates a version and capabilities", Initialize);
        runner.Add(Area, "a session opens and takes a prompt", PromptLifecycle);
        runner.Add(Area, "session/prompt acknowledges, and the stop reason arrives by notification", StopReasonArrivesLate);
        runner.Add(Area, "a permission request reaches the client and its answer returns", PermissionRoundTrip);
        runner.Add(Area, "an unknown session update survives the round trip", UnknownUpdatePreserved);
        runner.Add(Area, "tool call patch fields distinguish omitted from cleared", PatchSemantics);
        runner.Add(Area, "session/cancel ends the turn with stopReason cancelled", CancelEndsTheTurn);
    }

    /// <summary>Stand up both halves and hand back the client's connection.</summary>
    private static async Task<(AgentConnection Client, FakeAgent Agent, FakeClient Handler, Func<Task> Stop)> ConnectAsync()
    {
        var clientToAgent = new LoopbackStream();
        var agentToClient = new LoopbackStream();

        var handler = new FakeClient();
        var agentImpl = new FakeAgent();

        var client = AgentConnection.Create(agentToClient, clientToAgent, handler);
        var agent = ClientConnection.Create(clientToAgent, agentToClient, agentImpl);
        agentImpl.Attach(agent);

        var shutdown = new CancellationTokenSource();
        var clientPump = client.RunAsync(shutdown.Token);
        var agentPump = agent.RunAsync(shutdown.Token);

        return (client, agentImpl, handler, async () =>
        {
            await shutdown.CancelAsync();
            clientToAgent.Finish();
            agentToClient.Finish();
            await Task.WhenAny(Task.WhenAll(clientPump, agentPump), Task.Delay(500));
            shutdown.Dispose();
        });
    }

    private static async Task Initialize(CancellationToken ct)
    {
        var (client, _, _, stop) = await ConnectAsync();
        try
        {
            var response = await client.InitializeAsync(
                new InitializeRequest
                {
                    ProtocolVersion = new ProtocolVersion(2),
                    Info = new Implementation { Name = "acpkit-conformance", Version = "0.1.0" },
                },
                ct);

            Expect.Equal(2, (int)(ushort)response.ProtocolVersion.Value, "negotiated protocol version");
            Expect.True(response.Capabilities?.Session is not null, "the agent advertises session capabilities");
        }
        finally
        {
            await stop();
        }
    }

    private static async Task PromptLifecycle(CancellationToken ct)
    {
        var (client, _, handler, stop) = await ConnectAsync();
        try
        {
            await client.InitializeAsync(NewInitialize(), ct);
            var session = await client.SessionNewAsync(new NewSessionRequest { Cwd = new AbsolutePath("/tmp") }, ct);
            Expect.True(session.SessionId.Value.Length > 0, "the agent returned a session id");

            await client.SessionPromptAsync(
                new PromptRequest
                {
                    SessionId = session.SessionId,
                    Prompt = [new ContentBlockText { Value = new TextContent { Text = "hello" } }],
                },
                ct);

            await handler.WaitForUpdatesAsync(3, ct);

            var kinds = handler.Updates.Select(u => u.Update.Discriminator).ToList();
            Expect.Contains("user_message", string.Join(",", kinds), "the agent acknowledges the user message");
            Expect.Contains("agent_message_chunk", string.Join(",", kinds), "the agent streams a reply");
        }
        finally
        {
            await stop();
        }
    }

    /// <summary>
    /// The single most important behavioural change in v2: <c>session/prompt</c> returns as soon
    /// as the turn is accepted, and the outcome arrives later as a <c>state_update</c>. A client
    /// that treats the response as the end of the turn reports completion before any work has
    /// happened.
    /// </summary>
    private static async Task StopReasonArrivesLate(CancellationToken ct)
    {
        var (client, _, handler, stop) = await ConnectAsync();
        try
        {
            await client.InitializeAsync(NewInitialize(), ct);
            var session = await client.SessionNewAsync(new NewSessionRequest { Cwd = new AbsolutePath("/tmp") }, ct);

            await client.SessionPromptAsync(
                new PromptRequest
                {
                    SessionId = session.SessionId,
                    Prompt = [new ContentBlockText { Value = new TextContent { Text = "hello" } }],
                },
                ct);

            await handler.WaitForUpdatesAsync(4, ct);

            // The state is the union's discriminator, so the sequence of states reads directly
            // off the variant types.
            var states = handler.Updates
                .Select(u => u.Update)
                .OfType<SessionUpdateStateUpdate>()
                .Select(u => u.Value)
                .ToList();

            Expect.True(states.Count >= 2, $"expected a running and an idle state update, saw {states.Count}");
            Expect.Equal("running", states[0].Discriminator, "first state");
            Expect.Equal("idle", states[^1].Discriminator, "final state");

            var idle = states[^1] as StateUpdateIdle;
            Expect.True(idle is not null, "the final state update is the idle variant");
            Expect.True(idle!.Value.StopReason.HasValue, "the idle update carries a stop reason");
            Expect.Equal("end_turn", idle.Value.StopReason.Value.Value, "stop reason");
        }
        finally
        {
            await stop();
        }
    }

    /// <summary>
    /// Cancellation is an ordinary ending, not an error.
    /// </summary>
    /// <remarks>
    /// A client that treats <c>session/cancel</c> as terminating the conversation, or that
    /// stops reading once it has sent one, misses the very update that confirms the agent
    /// stopped. v2 requires the agent to flush pending updates and then report idle carrying
    /// <c>stopReason: "cancelled"</c>, and that idle update is the only confirmation there is.
    /// </remarks>
    private static async Task CancelEndsTheTurn(CancellationToken ct)
    {
        var (client, agent, handler, stop) = await ConnectAsync();
        try
        {
            agent.HoldTurnUntilCancelled = true;

            await client.InitializeAsync(NewInitialize(), ct);
            var session = await client.SessionNewAsync(new NewSessionRequest { Cwd = new AbsolutePath("/tmp") }, ct);

            await client.SessionPromptAsync(
                new PromptRequest
                {
                    SessionId = session.SessionId,
                    Prompt = [new ContentBlockText { Value = new TextContent { Text = "work forever" } }],
                },
                ct);

            // Wait until the turn is genuinely under way, so the cancel cannot race the start.
            await handler.WaitForUpdatesAsync(2, ct);

            await client.SessionCancelAsync(new CancelSessionNotification { SessionId = session.SessionId }, ct);

            await handler.WaitForUpdatesAsync(3, ct);

            var idle = handler.Updates
                .Select(u => u.Update)
                .OfType<SessionUpdateStateUpdate>()
                .Select(u => u.Value)
                .OfType<StateUpdateIdle>()
                .LastOrDefault();

            Expect.True(idle is not null, "the turn ended with an idle state update");
            Expect.True(idle!.Value.StopReason.HasValue, "the idle update carries a stop reason");
            Expect.Equal("cancelled", idle.Value.StopReason.Value.Value, "stop reason after cancelling");
        }
        finally
        {
            await stop();
        }
    }

    private static async Task PermissionRoundTrip(CancellationToken ct)
    {
        var (client, agent, handler, stop) = await ConnectAsync();
        try
        {
            await client.InitializeAsync(NewInitialize(), ct);
            handler.PermissionOutcome = "allow-once";

            var answer = await agent.AskPermissionAsync(ct);
            Expect.True(handler.PermissionAsked, "the client saw the permission request");

            var selected = answer.Outcome as RequestPermissionOutcomeSelected;
            Expect.True(selected is not null, $"expected a selected outcome, got {answer.Outcome.Discriminator}");
            Expect.Equal("allow-once", selected!.Value.OptionId.Value, "the option the client chose");
        }
        finally
        {
            await stop();
        }
    }

    /// <summary>
    /// A newer agent sending an update kind this version has never heard of must not break the
    /// client. ACP v2 requires the payload to survive storage, replay, and proxying intact.
    /// </summary>
    private static async Task UnknownUpdatePreserved(CancellationToken ct)
    {
        var (client, agent, handler, stop) = await ConnectAsync();
        try
        {
            await client.InitializeAsync(NewInitialize(), ct);
            await agent.SendRawUpdateAsync(
                """{"sessionId":"s1","update":{"sessionUpdate":"_acme_hologram","payload":{"depth":3}}}""",
                ct);

            await handler.WaitForUpdatesAsync(1, ct);

            var unknown = handler.Updates[0].Update as SessionUpdateUnknown;
            Expect.True(unknown is not null, "an unrecognised update deserialises to the unknown variant");
            Expect.Equal("_acme_hologram", unknown!.Kind, "the discriminator is preserved");
            Expect.Equal(3, unknown.Raw.GetProperty("payload").GetProperty("depth").GetInt32(),
                "the payload is preserved verbatim");
        }
        finally
        {
            await stop();
        }
    }

    /// <summary>
    /// Omitted, null, and a value are three different instructions on a tool-call update. This
    /// is the distinction a plain nullable property cannot carry.
    /// </summary>
    private static Task PatchSemantics(CancellationToken ct)
    {
        var options = AcpJsonContext.Default.Options;

        var untouched = new ToolCallUpdate { ToolCallId = new ToolCallId("call-1") };
        var cleared = new ToolCallUpdate { ToolCallId = new ToolCallId("call-1"), Title = Patch<string>.Cleared };
        var replaced = new ToolCallUpdate { ToolCallId = new ToolCallId("call-1"), Title = Patch<string>.Set("Reading") };

        var untouchedJson = JsonSerializer.Serialize(untouched, AcpJsonContext.Default.ToolCallUpdate);
        var clearedJson = JsonSerializer.Serialize(cleared, AcpJsonContext.Default.ToolCallUpdate);
        var replacedJson = JsonSerializer.Serialize(replaced, AcpJsonContext.Default.ToolCallUpdate);

        Expect.True(!untouchedJson.Contains("title", StringComparison.Ordinal),
            $"an omitted patch field must not appear on the wire at all, but got {untouchedJson}");
        Expect.Contains("\"title\":null", clearedJson, "a cleared field is sent as an explicit null");
        Expect.Contains("\"title\":\"Reading\"", replacedJson, "a set field carries its value");

        // And back again: the three states must be distinguishable after a round trip, or the
        // client cannot tell "leave alone" from "clear".
        var back = JsonSerializer.Deserialize(untouchedJson, AcpJsonContext.Default.ToolCallUpdate)!;
        Expect.True(back.Title.IsUnset, "an absent field reads back as unset");

        back = JsonSerializer.Deserialize(clearedJson, AcpJsonContext.Default.ToolCallUpdate)!;
        Expect.True(back.Title.IsCleared, "an explicit null reads back as cleared, not as unset");

        back = JsonSerializer.Deserialize(replacedJson, AcpJsonContext.Default.ToolCallUpdate)!;
        Expect.Equal("Reading", back.Title.GetValueOrDefault(), "a value reads back as itself");

        _ = options;
        return Task.CompletedTask;
    }

    private static InitializeRequest NewInitialize() => new()
    {
        ProtocolVersion = new ProtocolVersion(2),
        Info = new Implementation { Name = "acpkit-conformance", Version = "0.1.0" },
    };
}
