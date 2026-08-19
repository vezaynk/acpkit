using System.Text.Json;

namespace AcpKit;

/// <summary>
/// Handles an inbound request and answers the JSON that becomes the <c>result</c> member.
/// </summary>
/// <remarks>
/// Returning pre-serialized UTF-8 keeps <see cref="AcpPeer"/> free of any knowledge of
/// protocol types: the generated dispatcher owns the <c>JsonTypeInfo</c> and does the
/// serializing, and the peer only splices the bytes into a response envelope. That is also
/// what keeps the whole path reflection-free.
/// Throw <see cref="AcpException"/> to answer with a specific JSON-RPC error.
/// </remarks>
public delegate ValueTask<ReadOnlyMemory<byte>> AcpRequestHandler(
    string method,
    JsonElement parameters,
    CancellationToken cancellationToken);

/// <summary>Handles an inbound notification. Nothing is sent back.</summary>
public delegate ValueTask AcpNotificationHandler(
    string method,
    JsonElement parameters,
    CancellationToken cancellationToken);

/// <summary>
/// Observes one inbound NDJSON frame, without its newline terminator.
/// </summary>
/// <remarks>
/// The memory is borrowed from the reader and is valid only for the duration of the call.
/// Copy it if it needs to outlive the handler. A host recording a verbatim transcript does
/// that here, before parse, so a junk banner and a well-formed message are captured the
/// same way.
/// </remarks>
public delegate void AcpFrameHandler(ReadOnlyMemory<byte> frame);

/// <summary>Configuration for an <see cref="AcpPeer"/>.</summary>
public sealed class AcpPeerOptions
{
    /// <summary>
    /// Invoked for every inbound request. When null, every request is refused with
    /// <see cref="AcpErrorCode.MethodNotFound"/>.
    /// </summary>
    public AcpRequestHandler? RequestHandler { get; init; }

    /// <summary>
    /// Invoked for every inbound notification, one at a time and in arrival order.
    /// </summary>
    /// <remarks>
    /// Order is guaranteed because ACP depends on it: <c>session/update</c> carries chunk
    /// appends and upserts whose meaning is positional, and replaying them out of order
    /// silently corrupts the reconstructed conversation. Handlers run on a dedicated pump
    /// rather than on the read loop, so a slow handler cannot stall responses to requests
    /// this peer has itself sent — which would otherwise deadlock any handler that needs to
    /// call back to the far side.
    /// </remarks>
    public AcpNotificationHandler? NotificationHandler { get; init; }

    /// <summary>
    /// Called with any line that is not valid JSON, and with any other non-fatal oddity.
    /// </summary>
    /// <remarks>
    /// Agents write to stdout for reasons unrelated to the protocol — version banners,
    /// npm/npx noise, deprecation warnings, stray debug prints. Treating a non-JSON line as
    /// fatal makes the client fail against binaries that work fine, so these are reported
    /// and skipped.
    /// </remarks>
    public Action<string>? OnDiagnostic { get; init; }

    /// <summary>
    /// Called with every inbound frame, before it is parsed, including empty lines and
    /// lines that are not JSON.
    /// </summary>
    /// <remarks>
    /// This is the transcript tee: a host that must keep the agent's stdout verbatim cannot
    /// reconstruct it from decoded messages, because banners, vendor notifications, and
    /// parse failures never become typed traffic. The frame does not include the newline
    /// that delimited it. Throwing from this handler faults the connection, the same as
    /// <see cref="OnDiagnostic"/>; a capture that must not affect the worker wraps itself.
    /// </remarks>
    public AcpFrameHandler? OnFrame { get; init; }
}
