using System.Text.Json;

namespace AcpKit;

/// <summary>Which ACP line an <c>initialize</c> response is written in.</summary>
public enum AcpProtocolShape
{
    /// <summary>Neither line's markers were present, or both were.</summary>
    Unknown,

    /// <summary>v1: <c>agentCapabilities</c> and <c>agentInfo</c>.</summary>
    V1,

    /// <summary>v2: <c>capabilities</c> and a required <c>info</c>.</summary>
    V2,
}

/// <summary>
/// Works out which protocol line an agent is actually speaking.
/// </summary>
/// <remarks>
/// <para>
/// The obvious approach — read <c>protocolVersion</c> from the <c>initialize</c> response and
/// switch on it — does not work against real agents. goose 1.46.0 echoes back whatever version
/// it was sent, answering <c>2</c> to a request for 2 and <c>99</c> to a request for 99, while
/// always replying with v1 field names. A client that trusted the number would try to read a v1
/// body as v2 and fail on the first field it could not find.
/// </para>
/// <para>
/// What does identify the line is the shape of the response, because v2 renamed the fields that
/// carry it: <c>agentCapabilities</c> became <c>capabilities</c> and <c>agentInfo</c> became a
/// required <c>info</c>. Those renames are unambiguous and cheap to test, which makes them a
/// better signal than the field that was supposed to be the signal.
/// </para>
/// <para>
/// Use <see cref="AcpPeer.SendRawRequestAsync"/> to perform <c>initialize</c> without committing
/// to a line, pass the result here, and only then pick the typed connection to build.
/// </para>
/// </remarks>
public static class AcpHandshake
{
    /// <summary>The method every ACP conversation opens with.</summary>
    public const string InitializeMethod = "initialize";

    /// <summary>
    /// Which line the response is written in, judged by its field names.
    /// </summary>
    public static AcpProtocolShape DetectShape(JsonElement initializeResult)
    {
        if (initializeResult.ValueKind != JsonValueKind.Object)
        {
            return AcpProtocolShape.Unknown;
        }

        var v1 = initializeResult.TryGetProperty("agentCapabilities", out _)
            || initializeResult.TryGetProperty("agentInfo", out _);

        // v2 requires `info`, so its presence is decisive. `capabilities` alone is weaker but
        // still only ever appears in v2, where v1 spells the same idea `agentCapabilities`.
        var v2 = initializeResult.TryGetProperty("info", out _)
            || initializeResult.TryGetProperty("capabilities", out _);

        return (v1, v2) switch
        {
            (true, false) => AcpProtocolShape.V1,
            (false, true) => AcpProtocolShape.V2,

            // Both means an agent emitting v1 and v2 markers at once, which no version of the
            // spec sanctions; neither means a response this library does not recognise. Either
            // way the caller should not guess, so say so rather than picking a side.
            _ => AcpProtocolShape.Unknown,
        };
    }

    /// <summary>
    /// The <c>protocolVersion</c> the agent reported, which is advisory.
    /// </summary>
    /// <remarks>
    /// Worth reading and worth logging — an agent that reports a version it does not speak is
    /// something an operator should know about — but not worth branching on. Compare it against
    /// <see cref="DetectShape"/> and trust the shape.
    /// </remarks>
    public static int? DeclaredVersion(JsonElement initializeResult) =>
        initializeResult.ValueKind == JsonValueKind.Object
        && initializeResult.TryGetProperty("protocolVersion", out var version)
        && version.TryGetInt32(out var value)
            ? value
            : null;

    /// <summary>
    /// True when the agent reported a version whose line it is not actually speaking.
    /// </summary>
    /// <remarks>
    /// Not an error to fail on — the connection works fine, because the shape is what this
    /// library reads — but exactly the kind of thing worth surfacing once, since it means the
    /// agent's own version reporting cannot be relied on for anything else either.
    /// </remarks>
    public static bool VersionDisagreesWithShape(JsonElement initializeResult)
    {
        var declared = DeclaredVersion(initializeResult);
        if (declared is null)
        {
            return false;
        }

        return DetectShape(initializeResult) switch
        {
            AcpProtocolShape.V1 => declared != 1,
            AcpProtocolShape.V2 => declared != 2,
            _ => false,
        };
    }
}
