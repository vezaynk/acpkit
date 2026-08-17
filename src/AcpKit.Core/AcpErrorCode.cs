namespace AcpKit;

/// <summary>
/// JSON-RPC 2.0 error codes, plus the one ACP defines on top of them.
/// </summary>
public static class AcpErrorCode
{
    /// <summary>Invalid JSON was received. JSON-RPC 2.0 §5.1.</summary>
    public const int ParseError = -32700;

    /// <summary>The JSON sent is not a valid request object. JSON-RPC 2.0 §5.1.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>The method does not exist or is not available. JSON-RPC 2.0 §5.1.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>Invalid method parameters. JSON-RPC 2.0 §5.1.</summary>
    public const int InvalidParams = -32602;

    /// <summary>Internal JSON-RPC error. JSON-RPC 2.0 §5.1.</summary>
    public const int InternalError = -32603;

    /// <summary>
    /// The agent requires authentication before the requested operation. ACP-specific.
    /// </summary>
    /// <remarks>
    /// A clean <c>initialize</c> does not reveal that an agent needs this — the refusal
    /// arrives later, on <c>session/new</c>. Clients that only inspect the handshake will
    /// conclude authentication is optional and then fail to open a session.
    /// </remarks>
    public const int AuthRequired = -32000;
}
