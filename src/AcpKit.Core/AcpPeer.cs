using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace AcpKit;

/// <summary>
/// One end of a JSON-RPC 2.0 conversation carried over newline-delimited JSON.
/// </summary>
/// <remarks>
/// <para>
/// Symmetric by construction: ACP peers both call and are called, so there is no client or
/// agent here. The generated <c>Connection</c> types supply the typed surface on top.
/// </para>
/// <para>
/// The peer knows nothing about ACP itself — no methods, no models, no versions. It moves
/// framed JSON in both directions and matches responses to requests. Everything protocol-
/// shaped lives in the generated assemblies, which is what lets one transport serve v1 and
/// v2 at once.
/// </para>
/// </remarks>
public sealed class AcpPeer : IAsyncDisposable
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 256 };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly AcpPeerOptions _options;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<RequestId, TaskCompletionSource<Reply>> _pending = new();
    private readonly ConcurrentDictionary<RequestId, CancellationTokenSource> _inbound = new();
    private readonly Channel<PendingNotification> _notifications =
        Channel.CreateUnbounded<PendingNotification>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _nextId;
    private int _disposed;

    /// <summary>Create a peer over a read stream and a write stream.</summary>
    /// <param name="input">Where the far side's messages arrive. For a subprocess agent, its stdout.</param>
    /// <param name="output">Where this side's messages go. For a subprocess agent, its stdin.</param>
    /// <param name="options">Handlers and diagnostics.</param>
    public AcpPeer(Stream input, Stream output, AcpPeerOptions? options = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _options = options ?? new AcpPeerOptions();
    }

    /// <summary>
    /// Completes when the conversation ends, faulting if it ended badly.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Pump messages until the input stream ends or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var pump = Task.Run(() => PumpNotificationsAsync(linked.Token), CancellationToken.None);

        try
        {
            await ReadLoopAsync(linked.Token).ConfigureAwait(false);
            FailPending(new AcpException(AcpErrorCode.InternalError, "The peer closed the connection."));
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FailPending(new OperationCanceledException("The connection was cancelled."));
            _completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception e)
        {
            FailPending(e);
            _completion.TrySetException(e);
            throw;
        }
        finally
        {
            _notifications.Writer.TryComplete();
            await _lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: shutting down is how the pump ends.
            }
        }
    }

    /// <summary>Call a method on the far side and await its result.</summary>
    /// <remarks>
    /// There is no default timeout, and that is deliberate. A <c>session/prompt</c> turn
    /// legitimately runs for hours while a model works, and any timeout short enough to
    /// catch a hung agent is also short enough to abandon a healthy one mid-thought. Callers
    /// that want a deadline pass a <paramref name="cancellationToken"/> carrying one; doing
    /// so also sends <c>$/cancel_request</c>, so the far side learns to stop working.
    /// </remarks>
    public async Task<TResult> SendRequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        JsonTypeInfo<TParams> parametersTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        var id = RequestId.FromNumber(Interlocked.Increment(ref _nextId));
        var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var payload = AcpPayload.Serialize(parameters, parametersTypeInfo);
            await WriteAsync(w => WriteRequest(w, id, method, payload.Span), cancellationToken).ConfigureAwait(false);

            Reply reply;
            await using (cancellationToken.Register(() => OnCallerCancelled(id, completion)).ConfigureAwait(false))
            {
                reply = await completion.Task.ConfigureAwait(false);
            }

            using var document = JsonDocument.Parse(reply.Payload, DocumentOptions);
            return AcpPayload.Deserialize(document.RootElement, resultTypeInfo);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Call a method with raw JSON in and raw JSON out.
    /// </summary>
    /// <remarks>
    /// For the one call that cannot be typed in advance: <c>initialize</c>, whose response is
    /// what tells you which protocol line the agent speaks and therefore which typed connection
    /// to build. Also the escape hatch for <c>_</c>-prefixed vendor methods, which by definition
    /// have no schema.
    /// </remarks>
    /// <param name="method">The method name.</param>
    /// <param name="parametersJson">The <c>params</c> value, already serialized.</param>
    /// <param name="cancellationToken">Cancels the call, announcing it with <c>$/cancel_request</c>.</param>
    /// <returns>The <c>result</c> value. The caller owns the document and must dispose it.</returns>
    public async Task<JsonDocument> SendRawRequestAsync(
        string method,
        ReadOnlyMemory<byte> parametersJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        var id = RequestId.FromNumber(Interlocked.Increment(ref _nextId));
        var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await WriteAsync(w => WriteRequest(w, id, method, parametersJson.Span), cancellationToken)
                .ConfigureAwait(false);

            Reply reply;
            await using (cancellationToken.Register(() => OnCallerCancelled(id, completion)).ConfigureAwait(false))
            {
                reply = await completion.Task.ConfigureAwait(false);
            }

            return JsonDocument.Parse(reply.Payload, DocumentOptions);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Send a notification. Nothing comes back, by definition.</summary>
    public async Task SendNotificationAsync<TParams>(
        string method,
        TParams parameters,
        JsonTypeInfo<TParams> parametersTypeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        var payload = AcpPayload.Serialize(parameters, parametersTypeInfo);
        await WriteAsync(w => WriteNotification(w, method, payload.Span), cancellationToken).ConfigureAwait(false);
    }

    private void OnCallerCancelled(RequestId id, TaskCompletionSource<Reply> completion)
    {
        if (!completion.TrySetCanceled())
        {
            return;
        }

        // Best effort: tell the far side to stop. It may already have answered, and it is
        // free to ignore us, so a failure here is not worth surfacing to the caller.
        _ = Task.Run(async () =>
        {
            try
            {
                await WriteAsync(w => WriteCancelRequest(w, id), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Diagnostic($"Could not send $/cancel_request for id {id}: {e.Message}");
            }
        }, CancellationToken.None);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        using var reader = new AcpFrameReader(_input);

        while (true)
        {
            var line = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            // Before the empty-skip and before parse, so a recorder sees the stream as the
            // far side wrote it — including blank lines and banners that are not messages.
            _options.OnFrame?.Invoke(line.Value);

            if (line.Value.IsEmpty)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line.Value, DocumentOptions);
            }
            catch (JsonException e)
            {
                Diagnostic($"Ignoring a line that is not JSON: {e.Message}");
                continue;
            }

            DispatchDocument(document, cancellationToken);
        }
    }

    private void DispatchDocument(JsonDocument document, CancellationToken cancellationToken)
    {
        var root = document.RootElement;

        // JSON-RPC 2.0 §6, and required explicitly by ACP v2's transport section: a line may
        // carry a batch. Its responses go back as a single array, and notification-only
        // batches get no reply at all.
        if (root.ValueKind == JsonValueKind.Array)
        {
            _ = HandleBatchAsync(document, cancellationToken);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            Diagnostic($"Ignoring a JSON {root.ValueKind} where a message was expected.");
            document.Dispose();
            return;
        }

        if (TryCompleteResponse(root))
        {
            document.Dispose();
            return;
        }

        DispatchIncoming(document, root, cancellationToken, batched: false);
    }

    /// <summary>
    /// Match a response to the request that is waiting for it. Answers false when the
    /// message is not a response at all.
    /// </summary>
    private bool TryCompleteResponse(JsonElement root)
    {
        if (root.TryGetProperty("method", out _))
        {
            return false;
        }

        if (!root.TryGetProperty("id", out var idElement) || !RequestId.TryRead(idElement, out var id))
        {
            return false;
        }

        if (!_pending.TryGetValue(id, out var completion))
        {
            Diagnostic($"Ignoring a response for id {id}, which is not outstanding.");
            return true;
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            completion.TrySetException(AcpException.FromErrorObject(error));
            return true;
        }

        // A response with neither result nor error is malformed, but a missing result on a
        // success is common enough in the wild to treat as null rather than to reject.
        var result = root.TryGetProperty("result", out var resultElement)
            ? resultElement.GetRawText()
            : "null";

        completion.TrySetResult(new Reply(result));
        return true;
    }

    private void DispatchIncoming(JsonDocument document, JsonElement root, CancellationToken cancellationToken, bool batched)
    {
        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            Diagnostic("Ignoring a message with no method and no matching request.");
            if (!batched)
            {
                document.Dispose();
            }

            return;
        }

        var method = methodElement.GetString()!;
        var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;
        var hasId = root.TryGetProperty("id", out var idElement) && RequestId.TryRead(idElement, out _);

        if (!hasId)
        {
            if (method == "$/cancel_request")
            {
                CancelInbound(parameters);
                if (!batched)
                {
                    document.Dispose();
                }

                return;
            }

            // Cloned because the document dies with this stack frame while the pump does not.
            var cloned = parameters.ValueKind == JsonValueKind.Undefined ? default : parameters.Clone();
            if (!_notifications.Writer.TryWrite(new PendingNotification(method, cloned)))
            {
                Diagnostic($"Dropping notification {method}: the connection is shutting down.");
            }

            if (!batched)
            {
                document.Dispose();
            }

            return;
        }

        RequestId.TryRead(idElement, out var id);
        var clonedParams = parameters.ValueKind == JsonValueKind.Undefined ? default : parameters.Clone();
        if (!batched)
        {
            document.Dispose();
        }

        _ = HandleRequestAsync(id, method, clonedParams, cancellationToken);
    }

    private async Task HandleRequestAsync(RequestId id, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var payload = await InvokeRequestHandlerAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteAsync(w => WriteReply(w, id, payload), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Diagnostic($"Could not answer {method}: {e.Message}");
        }
    }

    private async Task<ReplyPayload> InvokeRequestHandlerAsync(
        RequestId id,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (_options.RequestHandler is not { } handler)
        {
            return ReplyPayload.Error(AcpErrorCode.MethodNotFound, $"This peer does not handle requests ({method}).");
        }

        using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inbound[id] = perRequest;

        try
        {
            var result = await handler(method, parameters, perRequest.Token).ConfigureAwait(false);
            return ReplyPayload.Success(result);
        }
        catch (AcpException e)
        {
            return ReplyPayload.Error(e.Code, e.Message, e.ErrorData);
        }
        catch (OperationCanceledException) when (perRequest.IsCancellationRequested)
        {
            return ReplyPayload.Error(AcpErrorCode.InternalError, $"{method} was cancelled.");
        }
        catch (Exception e)
        {
            // Deliberately terse. An unexpected failure inside a handler is this side's
            // problem, and its message may name internals that should not cross the wire.
            Diagnostic($"Handler for {method} threw: {e}");
            return ReplyPayload.Error(AcpErrorCode.InternalError, $"{method} failed.");
        }
        finally
        {
            _inbound.TryRemove(id, out _);
        }
    }

    private void CancelInbound(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("id", out var idElement)
            || !RequestId.TryRead(idElement, out var id))
        {
            Diagnostic("Ignoring $/cancel_request with no readable id.");
            return;
        }

        if (_inbound.TryGetValue(id, out var source))
        {
            source.Cancel();
        }
    }

    private async Task HandleBatchAsync(JsonDocument document, CancellationToken cancellationToken)
    {
        try
        {
            var replies = new List<Task<BatchReply>>();

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    // §6 wants a per-entry error for a malformed member, and a batch entry
                    // that is not an object has no id to answer against, so id is null.
                    replies.Add(Task.FromResult(new BatchReply(
                        null,
                        ReplyPayload.Error(AcpErrorCode.InvalidRequest, "Batch entry is not an object."))));
                    continue;
                }

                if (TryCompleteResponse(entry))
                {
                    continue;
                }

                if (!entry.TryGetProperty("method", out var methodElement)
                    || methodElement.ValueKind != JsonValueKind.String)
                {
                    replies.Add(Task.FromResult(new BatchReply(
                        null,
                        ReplyPayload.Error(AcpErrorCode.InvalidRequest, "Batch entry has no method."))));
                    continue;
                }

                var method = methodElement.GetString()!;
                var parameters = entry.TryGetProperty("params", out var p) ? p.Clone() : default;
                var hasId = entry.TryGetProperty("id", out var idElement) && RequestId.TryRead(idElement, out _);

                if (!hasId)
                {
                    if (method == "$/cancel_request")
                    {
                        CancelInbound(parameters);
                    }
                    else if (!_notifications.Writer.TryWrite(new PendingNotification(method, parameters)))
                    {
                        Diagnostic($"Dropping batched notification {method}: the connection is shutting down.");
                    }

                    continue;
                }

                RequestId.TryRead(idElement, out var id);
                replies.Add(InvokeBatchEntryAsync(id, method, parameters, cancellationToken));
            }

            if (replies.Count == 0)
            {
                // A batch of nothing but notifications gets no response at all. §6.
                return;
            }

            var completed = await Task.WhenAll(replies).ConfigureAwait(false);
            await WriteAsync(w => WriteBatchReply(w, completed), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Diagnostic($"Could not answer a batch: {e.Message}");
        }
        finally
        {
            document.Dispose();
        }
    }

    private async Task<BatchReply> InvokeBatchEntryAsync(
        RequestId id,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var payload = await InvokeRequestHandlerAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);
        return new BatchReply(id, payload);
    }

    private async Task PumpNotificationsAsync(CancellationToken cancellationToken)
    {
        var reader = _notifications.Reader;

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var notification))
            {
                if (_options.NotificationHandler is not { } handler)
                {
                    continue;
                }

                try
                {
                    await handler(notification.Method, notification.Parameters, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    // A failed notification handler must not take down the connection: there
                    // is nobody to report it to, and the conversation is still valid.
                    Diagnostic($"Handler for notification {notification.Method} threw: {e}");
                }
            }
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var (id, completion) in _pending)
        {
            if (exception is OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            else
            {
                completion.TrySetException(exception);
            }

            _pending.TryRemove(id, out _);
        }
    }

    private void Diagnostic(string message) => _options.OnDiagnostic?.Invoke(message);

    private async Task WriteAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var buffer = new PooledBuffer();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = false }))
            {
                write(writer);
            }

            buffer.WriteByte((byte)'\n');
            await _output.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void WriteRequest(Utf8JsonWriter w, RequestId id, string method, ReadOnlySpan<byte> parameters)
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        id.Write(w, "id");
        w.WriteString("method", method);
        w.WritePropertyName("params");
        w.WriteRawValue(parameters, skipInputValidation: true);
        w.WriteEndObject();
    }

    private static void WriteNotification(Utf8JsonWriter w, string method, ReadOnlySpan<byte> parameters)
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WriteString("method", method);
        w.WritePropertyName("params");
        w.WriteRawValue(parameters, skipInputValidation: true);
        w.WriteEndObject();
    }

    private static void WriteCancelRequest(Utf8JsonWriter w, RequestId id)
    {
        w.WriteStartObject();
        w.WriteString("jsonrpc", "2.0");
        w.WriteString("method", "$/cancel_request");
        w.WriteStartObject("params");
        id.Write(w, "id");
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteReply(Utf8JsonWriter w, RequestId id, ReplyPayload payload)
    {
        w.WriteStartObject();
        WriteReplyBody(w, id, payload);
        w.WriteEndObject();
    }

    private static void WriteBatchReply(Utf8JsonWriter w, BatchReply[] replies)
    {
        w.WriteStartArray();
        foreach (var reply in replies)
        {
            w.WriteStartObject();
            WriteReplyBody(w, reply.Id, reply.Payload);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteReplyBody(Utf8JsonWriter w, RequestId? id, ReplyPayload payload)
    {
        w.WriteString("jsonrpc", "2.0");

        if (id is { } value)
        {
            value.Write(w, "id");
        }
        else
        {
            w.WriteNull("id");
        }

        if (payload.IsError)
        {
            w.WriteStartObject("error");
            w.WriteNumber("code", payload.Code);
            w.WriteString("message", payload.Message!);
            if (payload.Data is { } data)
            {
                w.WritePropertyName("data");
                data.WriteTo(w);
            }

            w.WriteEndObject();
        }
        else
        {
            w.WritePropertyName("result");
            w.WriteRawValue(payload.ResultJson.Span, skipInputValidation: true);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _notifications.Writer.TryComplete();
        FailPending(new ObjectDisposedException(nameof(AcpPeer)));
        _lifetime.Dispose();
        _writeLock.Dispose();
    }

    private readonly record struct Reply(string Payload);

    private readonly record struct PendingNotification(string Method, JsonElement Parameters);

    private readonly record struct BatchReply(RequestId? Id, ReplyPayload Payload);

    private readonly record struct ReplyPayload(
        bool IsError,
        ReadOnlyMemory<byte> ResultJson,
        int Code,
        string? Message,
        JsonElement? Data)
    {
        public static ReplyPayload Success(ReadOnlyMemory<byte> result) => new(false, result, 0, null, null);

        public static ReplyPayload Error(int code, string message, JsonElement? data = null) =>
            new(true, default, code, message, data);
    }
}
