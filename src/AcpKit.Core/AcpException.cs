using System.Text.Json;

namespace AcpKit;

/// <summary>
/// A JSON-RPC error, either received from the peer or thrown by a handler to be sent to it.
/// </summary>
/// <remarks>
/// Throwing this from a request handler is the supported way to answer with a specific
/// error code: the peer maps it to a JSON-RPC error object verbatim. Any other exception
/// escaping a handler becomes <see cref="AcpErrorCode.InternalError"/> with no detail, so
/// that an unexpected failure cannot leak internals to the far side.
/// </remarks>
public sealed class AcpException : Exception
{
    /// <summary>Create an error with a code and message.</summary>
    public AcpException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Create an error with a code, message, and structured <c>data</c> payload.</summary>
    public AcpException(int code, string message, JsonElement? data)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    /// <summary>Create an error wrapping an underlying failure.</summary>
    public AcpException(int code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>The JSON-RPC error code. See <see cref="AcpErrorCode"/>.</summary>
    public int Code { get; }

    /// <summary>
    /// The optional <c>data</c> member of the error object. Named <c>ErrorData</c> because
    /// <see cref="Exception.Data"/> is already taken by an unrelated dictionary.
    /// </summary>
    public JsonElement? ErrorData { get; }

    /// <summary>True when the peer is telling us to authenticate first.</summary>
    public bool IsAuthRequired => Code == AcpErrorCode.AuthRequired;

    /// <summary>Read an error object — the <c>error</c> member of a response — into an exception.</summary>
    public static AcpException FromErrorObject(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed)
            ? parsed
            : AcpErrorCode.InternalError;

        var message = error.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()!
                : "The peer returned an error with no message.";

        // Clone: the JsonDocument backing this element is disposed as soon as the read loop
        // moves on, and the exception outlives it by design.
        JsonElement? data = error.TryGetProperty("data", out var dataElement)
            ? dataElement.Clone()
            : null;

        return new AcpException(code, message, data);
    }
}
