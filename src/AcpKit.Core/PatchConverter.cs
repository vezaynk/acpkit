using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AcpKit;

/// <summary>
/// Reads and writes <see cref="Patch{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Generated properties reference this as a closed generic —
/// <c>[JsonConverter(typeof(PatchConverter&lt;string&gt;))]</c> — rather than through a
/// <see cref="JsonConverterFactory"/>. A factory would have to construct the closed type at
/// runtime, which is exactly the reflection that native AOT cannot follow. Naming the
/// instantiation in an attribute makes it statically reachable instead.
/// </para>
/// <para>
/// Only two of the three states ever reach this converter. <see cref="Patch{T}.Unset"/> is
/// <c>default</c>, and the generated property carries
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]</c>, so an omitted
/// field is never written at all — which is precisely what "omitted" has to mean on the wire.
/// </para>
/// </remarks>
/// <typeparam name="T">The value type when the field is set.</typeparam>
public sealed class PatchConverter<T> : JsonConverter<Patch<T>>
{
    /// <inheritdoc/>
    public override Patch<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Reaching the converter at all means the property was present, so the only question
        // is whether it carried null. Absence is handled by the property never being visited.
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Patch<T>.Cleared;
        }

        var value = JsonSerializer.Deserialize(ref reader, TypeInfo(options));
        return value is null ? Patch<T>.Cleared : Patch<T>.Set(value);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Patch<T> value, JsonSerializerOptions options)
    {
        if (value.TryGetValue(out var inner))
        {
            JsonSerializer.Serialize(writer, inner, TypeInfo(options));
            return;
        }

        // Cleared, and — should the ignore condition ever be dropped — unset, both of which
        // are written as an explicit null. Unset must be omitted by the property, not by this
        // converter, which never learns the difference between "absent" and "present as null".
        writer.WriteNullValue();
    }

    /// <summary>
    /// The contract for <typeparamref name="T"/>, taken from the caller's options.
    /// </summary>
    /// <remarks>
    /// Resolving through <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> keeps this on
    /// the source-generated path: the options carry a generated context, the context already
    /// knows <typeparamref name="T"/> because the generator registered every type it emits,
    /// and no reflection-based contract is ever built.
    /// </remarks>
    private static JsonTypeInfo<T> TypeInfo(JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
