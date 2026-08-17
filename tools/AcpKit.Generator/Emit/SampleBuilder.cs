using System.Text.Json.Nodes;
using AcpKit.Generator.Model;

namespace AcpKit.Generator.Emit;

/// <summary>One synthetic payload, labelled with the type it is meant to be read as.</summary>
internal sealed record Sample(string TypeName, string Label, JsonNode Payload);

/// <summary>
/// Builds a representative payload for every type the protocol defines.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns "exercise every construct" from an intention into a fact. Hand-written
/// fixtures cover the types someone thought of; these are derived from the model itself, so a
/// type that exists is a type that gets round-tripped, and a type added by an upstream schema
/// bump arrives already covered.
/// </para>
/// <para>
/// Samples are deliberately maximal rather than minimal — every optional property is
/// populated, and every union arm gets its own sample. A minimal payload would exercise the
/// required path only, which is the path least likely to be wrong.
/// </para>
/// </remarks>
internal sealed class SampleBuilder(EmitPlan plan)
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    private readonly Dictionary<string, EmittedType> _types =
        plan.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>A payload for every type, plus one per union arm.</summary>
    public IReadOnlyList<Sample> Build()
    {
        var samples = new List<Sample>();

        foreach (var type in plan.Types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            switch (type)
            {
                case UnionType union:
                    foreach (var variant in union.Variants)
                    {
                        if (Value(type.Name, Empty, variant.DiscriminatorValue) is { } payload)
                        {
                            samples.Add(new Sample(union.Name, $"{union.Name}:{variant.DiscriminatorValue}", payload));
                        }
                    }

                    break;

                case ShapeUnionType shape:
                    foreach (var arm in shape.Arms)
                    {
                        if (Value(arm.Type.Name, Empty) is { } payload)
                        {
                            samples.Add(new Sample(shape.Name, $"{shape.Name}:{arm.Type.Name}", payload));
                        }
                    }

                    break;

                case OpenEnumType openEnum:
                    foreach (var member in openEnum.Members)
                    {
                        samples.Add(new Sample(openEnum.Name, $"{openEnum.Name}:{member.WireValue}", JsonValue.Create(member.WireValue)));
                    }

                    // ACP reserves the leading underscore for vendor values and requires them to
                    // survive a round trip. A closed enum could not represent this at all, so it
                    // is worth asserting on every enum rather than on a chosen few.
                    samples.Add(new Sample(openEnum.Name, $"{openEnum.Name}:_extension", JsonValue.Create("_acpkit_probe")));
                    break;

                case AliasType alias when alias.Underlying.Name == "System.Text.Json.JsonElement"
                                          || !(alias.Underlying.IsValueType || alias.Underlying.Name == "string"):
                    // Declares no C# type of its own, so there is nothing to round-trip.
                    break;

                default:
                    if (Value(type.Name, Empty) is { } value)
                    {
                        samples.Add(new Sample(type.Name, type.Name, value));
                    }

                    break;
            }
        }

        return samples;
    }

    /// <summary>
    /// A payload for a named type.
    /// </summary>
    /// <param name="visiting">
    /// Types currently being built, to break reference cycles. A cycle is resolved by dropping
    /// the optional property that closes it; a required one keeps its shape but stops
    /// recursing, which is the best a finite sample can do.
    /// </param>
    /// <param name="discriminator">For a union, which arm to build.</param>
    private JsonNode? Value(string typeName, IReadOnlySet<string> visiting, string? discriminator = null)
    {
        if (typeName == "System.Text.Json.JsonElement")
        {
            return new JsonObject { ["probe"] = JsonValue.Create("meta") };
        }

        if (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            var element = Value(typeName[..^2], visiting);
            return element is null ? new JsonArray() : new JsonArray(element);
        }

        if (!_types.TryGetValue(typeName, out var type))
        {
            return Primitive(typeName);
        }

        if (visiting.Contains(typeName))
        {
            return null;
        }

        var nested = new HashSet<string>(visiting, StringComparer.Ordinal) { typeName };

        switch (type)
        {
            case AliasType alias:
                return Value(alias.Underlying.Name, nested);

            case OpenEnumType openEnum:
                return JsonValue.Create(openEnum.Members.Count > 0 ? openEnum.Members[0].WireValue : "unknown");

            case ObjectType record:
                return Object(record.Properties, nested);

            case UnionType union:
                var variant = discriminator is null
                    ? union.Variants.FirstOrDefault()
                    : union.Variants.FirstOrDefault(v => v.DiscriminatorValue == discriminator);

                if (variant is null)
                {
                    return null;
                }

                var payload = (variant.PayloadType is { } carried ? Value(carried.Name, nested) : null) as JsonObject ?? [];
                payload[union.DiscriminatorJsonName] = JsonValue.Create(variant.DiscriminatorValue);
                foreach (var (key, node) in Object(union.BaseProperties, nested))
                {
                    payload[key] = node?.DeepClone();
                }

                return payload;

            case ShapeUnionType shape:
                return shape.Arms.Count > 0 ? Value(shape.Arms[0].Type.Name, nested) : null;

            case ValueUnionType value:
                return value.Arms.Count > 0 ? Value(value.Arms[0].Name, nested) : null;

            default:
                throw new NotSupportedException($"No sample builder for {type.GetType().Name}.");
        }
    }

    private JsonObject Object(IReadOnlyList<PropertyModel> properties, IReadOnlySet<string> visiting)
    {
        var result = new JsonObject();

        foreach (var property in properties)
        {
            var value = Value(property.Type.Name, visiting);

            if (value is null)
            {
                // The cycle closed here. Required properties still need something present, and
                // an empty object is the only shape that is always structurally valid.
                if (property.Required)
                {
                    result[property.JsonName] = new JsonObject();
                }

                continue;
            }

            result[property.JsonName] = value;
        }

        return result;
    }

    private static JsonNode? Primitive(string typeName) => typeName switch
    {
        "string" => JsonValue.Create("acpkit"),
        "bool" => JsonValue.Create(true),
        "double" => JsonValue.Create(1.5),
        "int" or "long" or "short" => JsonValue.Create(1),
        "uint" or "ulong" or "ushort" => JsonValue.Create(1),
        _ => null,
    };
}
