using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AcpKit;

/// <summary>
/// Serialization entry points for generated code.
/// </summary>
/// <remarks>
/// Every overload takes a <see cref="JsonTypeInfo{T}"/> rather than inferring a contract
/// from <see cref="Type"/>. That is the whole discipline behind AcpKit's AOT claim: the
/// reflection-based resolver is never reachable, so nothing in the serialization path can
/// be trimmed away and then fail at runtime.
/// </remarks>
public static class AcpPayload
{
    /// <summary>The UTF-8 JSON for <paramref name="value"/>.</summary>
    public static ReadOnlyMemory<byte> Serialize<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    /// <summary>
    /// Read <paramref name="element"/> as <typeparamref name="T"/>, failing with an
    /// <see cref="AcpException"/> carrying <see cref="AcpErrorCode.InvalidParams"/> rather
    /// than a raw <see cref="JsonException"/>, so a malformed peer message becomes a protocol
    /// error the far side can act on instead of an unhandled crash on this one.
    /// </summary>
    public static T Deserialize<T>(JsonElement element, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            var value = element.Deserialize(typeInfo);
            if (value is null)
            {
                throw new AcpException(AcpErrorCode.InvalidParams, $"Expected {typeof(T).Name}, received null.");
            }

            return value;
        }
        catch (JsonException e)
        {
            throw new AcpException(AcpErrorCode.InvalidParams, $"Could not read {typeof(T).Name}: {e.Message}", e);
        }
    }

    /// <summary>
    /// The UTF-8 JSON for an empty object, <c>{}</c>.
    /// </summary>
    /// <remarks>
    /// ACP leans on this more than most protocols. In v2 a successful <c>session/prompt</c>
    /// answers <c>{}</c> to acknowledge acceptance — the turn's outcome arrives later as a
    /// <c>state_update</c> notification — and every capability marker is an empty object
    /// whose mere presence means "supported".
    /// </remarks>
    public static ReadOnlyMemory<byte> EmptyObject => "{}"u8.ToArray();
}
