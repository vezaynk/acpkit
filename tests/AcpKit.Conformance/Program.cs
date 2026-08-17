using System.Text.Json;
using System.Text.Json.Serialization;
using AcpKit;
using AcpKit.Conformance;

Console.WriteLine("AcpKit conformance");

var runner = new Runner();
TransportScenarios.Register(runner);
ProtocolScenarios.Register(runner);

// One catalog per generated assembly. Naming them here rather than discovering them keeps the
// whole path reflection-free, which is the same discipline the library itself is held to.
CorpusScenarios.Register(
    runner,
    [
        new CorpusScenarios.Catalog(
            "v1-stable",
            AcpKit.Protocol.V1.ProtocolTypes.Names,
            AcpKit.Protocol.V1.ProtocolTypes.Find),
        new CorpusScenarios.Catalog(
            "v1-unstable",
            AcpKit.Protocol.V1.Unstable.ProtocolTypes.Names,
            AcpKit.Protocol.V1.Unstable.ProtocolTypes.Find),
        new CorpusScenarios.Catalog(
            "v2-stable",
            AcpKit.Protocol.V2.ProtocolTypes.Names,
            AcpKit.Protocol.V2.ProtocolTypes.Find),
        new CorpusScenarios.Catalog(
            "v2-unstable",
            AcpKit.Protocol.V2.Unstable.ProtocolTypes.Names,
            AcpKit.Protocol.V2.Unstable.ProtocolTypes.Find),
    ],
    Path.Combine(AppContext.BaseDirectory, "Corpus"));

return await runner.RunAllAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

namespace AcpKit.Conformance
{
    /// <summary>Payloads for exercising the transport. Nothing protocol-shaped lives here.</summary>
    internal sealed record Echo(string Text);

    internal sealed record EchoResult(string Text, int Length);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Echo))]
    [JsonSerializable(typeof(EchoResult))]
    internal sealed partial class StubContext : JsonSerializerContext;

    /// <summary>
    /// End-to-end exercises of <see cref="AcpPeer"/>: two live peers over a real duplex link,
    /// or one peer whose far side is driven by hand when the point is to send something a
    /// well-behaved peer never would.
    /// </summary>
    internal static class TransportScenarios
    {
        private const string Area = "transport";

        public static void Register(Runner runner)
        {
            runner.Add(Area, "a request gets its response", RequestResponse);
            runner.Add(Area, "notifications arrive in order", NotificationOrder);
            runner.Add(Area, "a slow notification handler does not block responses", SlowNotificationDoesNotBlock);
            runner.Add(Area, "an unhandled method is refused with -32601", MethodNotFound);
            runner.Add(Area, "a handler's AcpException crosses the wire intact", HandlerErrorCode);
            runner.Add(Area, "an unexpected handler failure becomes a terse -32603", HandlerLeaksNothing);
            runner.Add(Area, "auth_required is recognisable", AuthRequired);
            runner.Add(Area, "a response whose id came back as a string still matches", NumericStringId);
            runner.Add(Area, "a non-JSON line is skipped, not fatal", NonJsonLineTolerated);
            runner.Add(Area, "CRLF line endings are accepted", CrlfAccepted);
            runner.Add(Area, "a message larger than the read buffer round-trips", LargeMessage);
            runner.Add(Area, "a batch answers with one array and skips notifications", BatchMixed);
            runner.Add(Area, "a batch of only notifications gets no reply", BatchNotificationsOnly);
            runner.Add(Area, "a malformed batch entry answers -32600 against a null id", BatchMalformedEntry);
            runner.Add(Area, "cancelling a caller sends $/cancel_request", CallerCancellationIsAnnounced);
            runner.Add(Area, "an inbound $/cancel_request cancels the handler", InboundCancellation);
            runner.Add(Area, "a disconnect faults everything in flight", DisconnectFaultsPending);
        }

        private static AcpPeerOptions Echoing(Action<string>? diagnostic = null) => new()
        {
            RequestHandler = (method, parameters, _) =>
            {
                if (method != "echo")
                {
                    throw new AcpException(AcpErrorCode.MethodNotFound, $"No such method: {method}");
                }

                var echo = AcpPayload.Deserialize(parameters, StubContext.Default.Echo);
                var result = new EchoResult(echo.Text, echo.Text.Length);
                return ValueTask.FromResult(AcpPayload.Serialize(result, StubContext.Default.EchoResult));
            },
            OnDiagnostic = diagnostic,
        };


        /// <summary>
        /// A response envelope built by hand, so a scenario can send something a well-behaved
        /// peer never would — such as echoing the id back as a string.
        /// </summary>
        private static string EchoResponse(string rawId, string text) =>
            "{\"jsonrpc\":\"2.0\",\"id\":" + rawId
            + ",\"result\":{\"text\":\"" + text + "\",\"length\":" + text.Length + "}}";

        private static Task<EchoResult> EchoAsync(AcpPeer peer, string text, CancellationToken ct) =>
            peer.SendRequestAsync("echo", new Echo(text),
                StubContext.Default.Echo, StubContext.Default.EchoResult, ct);

        private static async Task RequestResponse(CancellationToken ct)
        {
            await using var link = Link.Create(right: Echoing());
            var result = await EchoAsync(link.Left, "hello", ct);
            Expect.Equal("hello", result.Text, "echoed text");
            Expect.Equal(5, result.Length, "echoed length");
        }

        private static async Task NotificationOrder(CancellationToken ct)
        {
            // Ordering is not incidental here. ACP session/update carries chunk appends whose
            // meaning is positional, so a transport that reorders them corrupts the transcript.
            var seen = new List<string>();
            var done = new TaskCompletionSource();
            var options = new AcpPeerOptions
            {
                NotificationHandler = (_, parameters, _) =>
                {
                    var echo = AcpPayload.Deserialize(parameters, StubContext.Default.Echo);
                    seen.Add(echo.Text);
                    if (seen.Count == 50)
                    {
                        done.TrySetResult();
                    }

                    return ValueTask.CompletedTask;
                },
            };

            await using var link = Link.Create(right: options);
            for (var i = 0; i < 50; i++)
            {
                await link.Left.SendNotificationAsync("note", new Echo(i.ToString()), StubContext.Default.Echo, ct);
            }

            await done.Task.WaitAsync(ct);
            Expect.Equal(string.Join(",", Enumerable.Range(0, 50)), string.Join(",", seen), "notification order");
        }

        private static async Task SlowNotificationDoesNotBlock(CancellationToken ct)
        {
            // The deadlock this guards: a notification handler that calls back to the far side
            // would, on a single-threaded pump, block the very read loop that must deliver its
            // answer. Notifications run on their own pump precisely so this terminates.
            var release = new TaskCompletionSource();
            var options = new AcpPeerOptions
            {
                NotificationHandler = async (_, _, _) => await release.Task,
                RequestHandler = Echoing().RequestHandler,
            };

            await using var link = Link.Create(right: options);
            await link.Left.SendNotificationAsync("note", new Echo("blocking"), StubContext.Default.Echo, ct);

            var result = await EchoAsync(link.Left, "still alive", ct);
            Expect.Equal("still alive", result.Text, "response while a notification handler is stuck");
            release.SetResult();
        }

        private static async Task MethodNotFound(CancellationToken ct)
        {
            await using var link = Link.Create(right: new AcpPeerOptions());
            var error = await Expect.ThrowsAsync<AcpException>(
                () => EchoAsync(link.Left, "x", ct), "a peer with no handler");
            Expect.Equal(AcpErrorCode.MethodNotFound, error.Code, "error code");
        }

        private static async Task HandlerErrorCode(CancellationToken ct)
        {
            await using var link = Link.Create(right: Echoing());
            var error = await Expect.ThrowsAsync<AcpException>(
                () => link.Left.SendRequestAsync("nope", new Echo("x"),
                    StubContext.Default.Echo, StubContext.Default.EchoResult, ct),
                "an unknown method");
            Expect.Equal(AcpErrorCode.MethodNotFound, error.Code, "error code");
            Expect.Contains("nope", error.Message, "error message");
        }

        private static async Task HandlerLeaksNothing(CancellationToken ct)
        {
            var options = new AcpPeerOptions
            {
                RequestHandler = (_, _, _) => throw new InvalidOperationException("connection string is Server=secret;"),
            };

            await using var link = Link.Create(right: options);
            var error = await Expect.ThrowsAsync<AcpException>(
                () => EchoAsync(link.Left, "x", ct), "a handler that threw");
            Expect.Equal(AcpErrorCode.InternalError, error.Code, "error code");
            Expect.True(
                !error.Message.Contains("secret", StringComparison.OrdinalIgnoreCase),
                $"an internal failure must not leak its message across the wire, but got: {error.Message}");
        }

        private static async Task AuthRequired(CancellationToken ct)
        {
            var options = new AcpPeerOptions
            {
                RequestHandler = (_, _, _) => throw new AcpException(AcpErrorCode.AuthRequired, "Authentication required"),
            };

            await using var link = Link.Create(right: options);
            var error = await Expect.ThrowsAsync<AcpException>(
                () => EchoAsync(link.Left, "x", ct), "an agent demanding authentication");
            Expect.True(error.IsAuthRequired, "IsAuthRequired");
        }

        private static async Task NumericStringId(CancellationToken ct)
        {
            // Real agents echo the id they were sent as a string. A correlation table that does
            // not normalise simply hangs: the reply arrives and matches nothing.
            await using var link = Link.CreateHalf();
            var call = EchoAsync(link.Left, "x", ct);

            var request = await link.ReadFromLeftAsync(ct);
            using var document = JsonDocument.Parse(request);
            var id = document.RootElement.GetProperty("id").GetInt64();

            link.RightToLeft.WriteRaw(EchoResponse("\"" + id + "\"", "x") + "\n");

            var result = await call;
            Expect.Equal("x", result.Text, "result correlated through a string id");
        }

        private static async Task NonJsonLineTolerated(CancellationToken ct)
        {
            // Agents print banners, npx noise, and deprecation warnings to stdout. Treating any
            // of that as fatal breaks the client against binaries that work fine.
            var diagnostics = new List<string>();
            await using var link = Link.CreateHalf(new AcpPeerOptions { OnDiagnostic = diagnostics.Add });
            var call = EchoAsync(link.Left, "x", ct);

            var request = await link.ReadFromLeftAsync(ct);
            using var document = JsonDocument.Parse(request);
            var id = document.RootElement.GetProperty("id").GetInt64();

            link.RightToLeft.WriteRaw("npm warn Unknown user config \"prefix\"\n");
            link.RightToLeft.WriteRaw("Agent v1.2.3 ready\n");
            link.RightToLeft.WriteRaw(EchoResponse(id.ToString(), "x") + "\n");

            var result = await call;
            Expect.Equal("x", result.Text, "the response after two junk lines");
            Expect.True(diagnostics.Count >= 2, $"both junk lines should be reported, saw {diagnostics.Count}");
        }

        private static async Task CrlfAccepted(CancellationToken ct)
        {
            await using var link = Link.CreateHalf();
            var call = EchoAsync(link.Left, "x", ct);

            var request = await link.ReadFromLeftAsync(ct);
            using var document = JsonDocument.Parse(request);
            var id = document.RootElement.GetProperty("id").GetInt64();

            link.RightToLeft.WriteRaw(EchoResponse(id.ToString(), "x") + "\r\n");

            var result = await call;
            Expect.Equal("x", result.Text, "a CRLF-terminated response");
        }

        private static async Task LargeMessage(CancellationToken ct)
        {
            // A session/update carrying a diff or a base64 terminal snapshot runs well past any
            // fixed buffer, so the reader has to grow rather than truncate.
            await using var link = Link.Create(right: Echoing());
            var big = new string('x', 512 * 1024);
            var result = await EchoAsync(link.Left, big, ct);
            Expect.Equal(big.Length, result.Length, "round-tripped length of a 512 KiB payload");
        }

        private static async Task BatchMixed(CancellationToken ct)
        {
            await using var link = Link.CreateHalf(Echoing());

            link.RightToLeft.WriteRaw("""
                [{"jsonrpc":"2.0","id":1,"method":"echo","params":{"text":"a"}},
                 {"jsonrpc":"2.0","method":"note","params":{"text":"ignored"}},
                 {"jsonrpc":"2.0","id":2,"method":"echo","params":{"text":"bb"}}]
                """.ReplaceLineEndings(string.Empty) + "\n");

            var reply = await link.ReadFromLeftAsync(ct);
            using var document = JsonDocument.Parse(reply);
            Expect.Equal(JsonValueKind.Array, document.RootElement.ValueKind, "a batch is answered with an array");
            Expect.Equal(2, document.RootElement.GetArrayLength(), "only the two requests are answered");

            var lengths = document.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("result").GetProperty("length").GetInt32())
                .OrderBy(n => n)
                .ToArray();
            Expect.Equal("1,2", string.Join(",", lengths), "both results present");
        }

        private static async Task BatchNotificationsOnly(CancellationToken ct)
        {
            var seen = new TaskCompletionSource();
            var options = new AcpPeerOptions
            {
                RequestHandler = Echoing().RequestHandler,
                NotificationHandler = (_, _, _) =>
                {
                    seen.TrySetResult();
                    return ValueTask.CompletedTask;
                },
            };

            await using var link = Link.CreateHalf(options);
            link.RightToLeft.WriteRaw(
                """[{"jsonrpc":"2.0","method":"note","params":{"text":"a"}}]""" + "\n");

            await seen.Task.WaitAsync(ct);

            // Then prove nothing was written back, by asking a question and seeing its answer first.
            link.RightToLeft.WriteRaw(
                """{"jsonrpc":"2.0","id":9,"method":"echo","params":{"text":"z"}}""" + "\n");
            var reply = await link.ReadFromLeftAsync(ct);
            using var document = JsonDocument.Parse(reply);
            Expect.Equal(9, document.RootElement.GetProperty("id").GetInt32(),
                "the first thing written back is the answer to id 9, so the batch produced nothing");
        }

        private static async Task BatchMalformedEntry(CancellationToken ct)
        {
            await using var link = Link.CreateHalf(Echoing());
            link.RightToLeft.WriteRaw("""[42]""" + "\n");

            var reply = await link.ReadFromLeftAsync(ct);
            using var document = JsonDocument.Parse(reply);
            var entry = document.RootElement[0];
            Expect.Equal(JsonValueKind.Null, entry.GetProperty("id").ValueKind, "id is null for an unaddressable entry");
            Expect.Equal(AcpErrorCode.InvalidRequest, entry.GetProperty("error").GetProperty("code").GetInt32(),
                "error code");
        }

        private static async Task CallerCancellationIsAnnounced(CancellationToken ct)
        {
            await using var link = Link.CreateHalf();
            using var caller = new CancellationTokenSource();

            var call = EchoAsync(link.Left, "x", caller.Token);
            await link.ReadFromLeftAsync(ct);

            await caller.CancelAsync();
            await Expect.ThrowsAsync<OperationCanceledException>(() => call, "a cancelled call");

            var announcement = await link.ReadFromLeftAsync(ct);
            Expect.Contains("$/cancel_request", announcement, "the far side is told to stop");
        }

        private static async Task InboundCancellation(CancellationToken ct)
        {
            var started = new TaskCompletionSource();
            var observed = new TaskCompletionSource<bool>();
            var options = new AcpPeerOptions
            {
                RequestHandler = async (_, _, token) =>
                {
                    started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        observed.TrySetResult(true);
                        throw;
                    }

                    return AcpPayload.EmptyObject;
                },
            };

            await using var link = Link.CreateHalf(options);
            link.RightToLeft.WriteRaw(
                """{"jsonrpc":"2.0","id":4,"method":"slow","params":{}}""" + "\n");
            await started.Task.WaitAsync(ct);

            link.RightToLeft.WriteRaw(
                """{"jsonrpc":"2.0","method":"$/cancel_request","params":{"id":4}}""" + "\n");

            Expect.True(await observed.Task.WaitAsync(ct), "the handler observed cancellation");
        }

        private static async Task DisconnectFaultsPending(CancellationToken ct)
        {
            // Without this, a caller whose agent crashed mid-turn waits forever on a reply that
            // can never arrive.
            var link = Link.CreateHalf();
            var call = EchoAsync(link.Left, "x", ct);
            await link.ReadFromLeftAsync(ct);

            link.RightToLeft.Finish();

            await Expect.ThrowsAsync<AcpException>(() => call, "a call in flight when the peer vanished");
            await link.DisposeAsync();
        }
    }
}
