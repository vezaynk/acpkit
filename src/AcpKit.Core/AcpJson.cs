using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AcpKit;

/// <summary>
/// Serialization helpers that generated union converters call into.
/// </summary>
/// <remarks>
/// Every one of these resolves its contract through
/// <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/>, which on a source-generated context
/// is a lookup in a generated table. Nothing here builds a contract by reflecting over a
/// <see cref="Type"/>, which is what keeps generated code trimmable and AOT-safe.
/// </remarks>
public static class AcpJson
{
    /// <summary>
    /// Write a value's properties directly into the object already being written, without the
    /// surrounding braces.
    /// </summary>
    /// <remarks>
    /// ACP unions are flattened on the wire: a <c>session/update</c> is one object carrying
    /// both the <c>sessionUpdate</c> discriminator and the variant's own fields, not a
    /// discriminator wrapping a nested payload. Emitting that shape means writing the
    /// discriminator and then splicing the payload's members in beside it.
    /// </remarks>
    public static void WriteMembers<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var element = JsonSerializer.SerializeToElement(value, TypeInfo<T>(options));
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            property.WriteTo(writer);
        }
    }

    /// <summary>Read an element as <typeparamref name="T"/>.</summary>
    /// <exception cref="AcpException">The payload does not match the expected shape.</exception>
    public static T Read<T>(JsonElement element, JsonSerializerOptions options)
    {
        try
        {
            return element.Deserialize(TypeInfo<T>(options))
                ?? throw new AcpException(AcpErrorCode.InvalidParams, $"Expected {typeof(T).Name}, received null.");
        }
        catch (JsonException e)
        {
            throw new AcpException(AcpErrorCode.InvalidParams, $"Could not read {typeof(T).Name}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Read a named member as <typeparamref name="T"/>, answering <c>default</c> when it is
    /// absent or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Used for the properties a union declares on itself, which are flattened alongside the
    /// variant's own fields — <c>DiffChange.path</c> applies whatever the <c>operation</c> is.
    /// </remarks>
    public static T? ReadOptional<T>(JsonElement parent, string name, JsonSerializerOptions options)
    {
        if (!parent.TryGetProperty(name, out var member) || member.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        return member.Deserialize(TypeInfo<T>(options));
    }

    /// <summary>
    /// Read a named member that the schema marks required.
    /// </summary>
    /// <exception cref="AcpException">The member is absent or null.</exception>
    public static T ReadRequired<T>(JsonElement parent, string name, JsonSerializerOptions options)
    {
        if (!parent.TryGetProperty(name, out var member) || member.ValueKind == JsonValueKind.Null)
        {
            throw new AcpException(AcpErrorCode.InvalidParams, $"Required property \"{name}\" is missing.");
        }

        return Read<T>(member, options);
    }

    /// <summary>
    /// Try to read an element as <typeparamref name="T"/>, answering false when it is not that
    /// shape. Used by unions resolved on JSON token kind, where trying is the only way to ask.
    /// </summary>
    public static bool TryRead<T>(JsonElement element, JsonSerializerOptions options, out T value)
    {
        try
        {
            value = element.Deserialize(TypeInfo<T>(options))!;
            return value is not null;
        }
        catch (JsonException)
        {
            value = default!;
            return false;
        }
        catch (NotSupportedException)
        {
            // System.Text.Json raises this for a shape the contract cannot represent at all,
            // which for a union arm is simply a "no", not a failure worth propagating.
            value = default!;
            return false;
        }
    }

    /// <summary>
    /// Read a named member as a three-state patch: absent, cleared, or set.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole of ACP v2's update model, and it can only be made here —
    /// once a value has been read as a plain nullable, "the sender omitted this" and "the
    /// sender cleared this" are indistinguishable.
    /// </remarks>
    public static Patch<T> ReadPatch<T>(JsonElement parent, string name, JsonSerializerOptions options)
    {
        if (!parent.TryGetProperty(name, out var member))
        {
            return Patch<T>.Unset;
        }

        if (member.ValueKind == JsonValueKind.Null)
        {
            return Patch<T>.Cleared;
        }

        return Patch<T>.Set(Read<T>(member, options));
    }

    /// <summary>
    /// Write a patch, omitting the property entirely when it is unset.
    /// </summary>
    /// <remarks>
    /// Omission has to be the absence of the property, not a null: sending null would tell the
    /// far side to clear a field the sender never touched.
    /// </remarks>
    public static void WritePatch<T>(Utf8JsonWriter writer, string name, Patch<T> value, JsonSerializerOptions options)
    {
        if (value.IsUnset)
        {
            return;
        }

        writer.WritePropertyName(name);

        if (value.TryGetValue(out var inner))
        {
            JsonSerializer.Serialize(writer, inner, TypeInfo<T>(options));
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
