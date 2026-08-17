using System.Text;
using AcpKit.Generator.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AcpKit.Generator.Emit;

/// <summary>
/// Emits the three kinds of union, and the converters that resolve them.
/// </summary>
/// <remarks>
/// <para>
/// Every converter here decides which arm applies by inspecting the payload — a discriminator
/// string, a set of required keys, or a JSON token kind — with the decision table computed at
/// generation time. None of them constructs a candidate and catches the failure, which is how
/// reflective SDKs do it and why they pick the wrong arm whenever two shapes overlap.
/// </para>
/// <para>
/// Each union also gets an <c>Unknown</c> arm holding the raw JSON. ACP v2 requires unknown
/// variants to survive being stored, replayed, and proxied, and an exception thrown at parse
/// time destroys the message instead. An old client meeting a newer agent should degrade to
/// "I cannot render this", not to "I cannot read anything".
/// </para>
/// </remarks>
internal static class UnionEmitter
{
    /// <summary>A union selected by a <c>const</c> discriminator property.</summary>
    public static IEnumerable<MemberDeclarationSyntax> EmitDiscriminated(UnionType type, Func<string, MemberDeclarationSyntax> parse)
    {
        var converter = type.Name + "Converter";
        var baseBody = new StringBuilder();

        foreach (var property in type.BaseProperties)
        {
            baseBody.Append(CSharpEmitter.EmitPropertyText(property)).Append('\n');
        }

        yield return parse($$"""
            {{CSharpEmitter.DocsText(type.Documentation)}}[JsonConverter(typeof({{converter}}))]
            public abstract class {{type.Name}}
            {
                /// <summary>The value of <c>{{CSharpEmitter.EscapeText(type.DiscriminatorJsonName)}}</c> that selects this variant.</summary>
                [JsonIgnore]
                public abstract string Discriminator { get; }

            {{baseBody}}}
            """);

        foreach (var variant in type.Variants)
        {
            if (variant.Inline)
            {
                // The payload derives from the union itself; there is nothing to wrap.
                continue;
            }

            var value = variant.PayloadType is { } carried
                ? "\n        /// <summary>The fields this variant carries, flattened alongside the discriminator on the wire.</summary>\n"
                  + $"        public required {carried.Name} Value {{ get; init; }}\n"
                : string.Empty;

            yield return parse($$"""
                {{CSharpEmitter.DocsText(variant.Documentation)}}public sealed class {{variant.CsName}} : {{type.Name}}
                {
                    /// <inheritdoc/>
                    [JsonIgnore]
                    public override string Discriminator => "{{variant.DiscriminatorValue}}";
                {{value}}}
                """);
        }

        yield return parse($$"""
            /// <summary>
            /// A <see cref="{{type.Name}}"/> variant this protocol version does not define.
            /// </summary>
            /// <remarks>
            /// Holds the payload verbatim so it can be stored, replayed, or proxied unchanged.
            /// ACP v2 requires that; failing to parse instead would lose the whole message.
            /// </remarks>
            public sealed class {{type.Name}}Unknown : {{type.Name}}
            {
                /// <inheritdoc/>
                [JsonIgnore]
                public override string Discriminator => Kind;

                /// <summary>The discriminator value that was received.</summary>
                public required string Kind { get; init; }

                /// <summary>The payload exactly as it arrived.</summary>
                public required JsonElement Raw { get; init; }
            }
            """);

        yield return parse(BuildDiscriminatedConverter(type, converter));
    }

    private static string BuildDiscriminatedConverter(UnionType type, string converter)
    {
        var reads = new StringBuilder();
        var assignments = new StringBuilder();

        foreach (var property in type.BaseProperties)
        {
            var local = Camel(property.CsName);
            // A patch field's three states are carried by Patch<T>, so the read has to produce
            // one. Reading the bare T would collapse "absent" and "cleared" into the same thing,
            // which is the distinction the whole update model rests on.
            var call = property.ThreeState
                ? $"AcpJson.ReadPatch<{property.Type.Name}>(root, \"{property.JsonName}\", options)"
                : $"AcpJson.{(property.Required ? "ReadRequired" : "ReadOptional")}<{property.Type.Name}>(root, \"{property.JsonName}\", options)";

            reads.Append($"""
                        var {local} = {call};

            """);
            assignments.Append($"{property.CsName} = {local}, ");
        }

        var shared = assignments.ToString();
        var cases = new StringBuilder();

        foreach (var variant in type.Variants)
        {
            var construct = (variant.Inline, variant.PayloadType) switch
            {
                (true, { } inlined) => $"AcpJson.Read<{inlined.Name}>(root, options)",
                (false, { } carried) => $"new {variant.CsName} {{ {shared}Value = AcpJson.Read<{carried.Name}>(root, options) }}",
                _ => $"new {variant.CsName} {{ {shared.TrimEnd(' ', ',')} }}",
            };

            cases.Append($$"""
                            "{{variant.DiscriminatorValue}}" => {{construct}},

            """);
        }

        var writes = new StringBuilder();
        foreach (var variant in type.Variants)
        {
            var caseType = variant.Inline ? variant.PayloadType!.Name : variant.CsName;
            var splice = variant.PayloadType is null
                ? "break;"
                : $"AcpJson.WriteMembers(writer, {(variant.Inline ? "typed" : "typed.Value")}, options);\n                            break;";

            writes.Append($$"""
                            case {{caseType}} typed:
                                {{splice}}

            """);
        }

        var baseWrites = new StringBuilder();
        foreach (var property in type.BaseProperties)
        {
            var local = Camel(property.CsName) + "Written";
            var write = property.ThreeState
                ? $$"""
                            AcpJson.WritePatch(writer, "{{property.JsonName}}", value.{{property.CsName}}, options);
                """
                : $$"""
                            if (value.{{property.CsName}} is { } {{local}})
                            {
                                writer.WritePropertyName("{{property.JsonName}}");
                                JsonSerializer.Serialize(writer, {{local}}, options.GetTypeInfo(typeof({{property.Type.Name}})));
                            }
                """;

            baseWrites.Append(write).Append('\n');
        }

        return $$"""
            /// <summary>Resolves <see cref="{{type.Name}}"/> by its <c>{{CSharpEmitter.EscapeText(type.DiscriminatorJsonName)}}</c> discriminator.</summary>
            internal sealed class {{converter}} : JsonConverter<{{type.Name}}>
            {
                /// <inheritdoc/>
                public override {{type.Name}} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    var root = document.RootElement;
                    var kind = root.TryGetProperty("{{type.DiscriminatorJsonName}}", out var marker) && marker.ValueKind == JsonValueKind.String
                        ? marker.GetString()
                        : null;

            {{reads}}        return kind switch
                    {
            {{cases}}            _ => new {{type.Name}}Unknown { {{shared}}Kind = kind ?? string.Empty, Raw = root.Clone() },
                    };
                }

                /// <inheritdoc/>
                public override void Write(Utf8JsonWriter writer, {{type.Name}} value, JsonSerializerOptions options)
                {
                    if (value is {{type.Name}}Unknown unknown)
                    {
                        unknown.Raw.WriteTo(writer);
                        return;
                    }

                    writer.WriteStartObject();
                    writer.WriteString("{{type.DiscriminatorJsonName}}", value.Discriminator);
            {{baseWrites}}
                    switch (value)
                    {
            {{writes}}            default:
                                break;
                    }

                    writer.WriteEndObject();
                }
            }
            """;
    }

    /// <summary>A union with no discriminator, resolved by which required keys are present.</summary>
    public static IEnumerable<MemberDeclarationSyntax> EmitShape(ShapeUnionType type, Func<string, MemberDeclarationSyntax> parse)
    {
        var converter = type.Name + "Converter";

        yield return parse($$"""
            {{CSharpEmitter.DocsText(type.Documentation)}}[JsonConverter(typeof({{converter}}))]
            public abstract class {{type.Name}}
            {
            }
            """);

        foreach (var arm in type.Arms)
        {
            yield return parse($$"""
                {{CSharpEmitter.DocsText(arm.Documentation)}}public sealed class {{type.Name}}{{arm.Type.Name}} : {{type.Name}}
                {
                    /// <summary>The value this arm carries.</summary>
                    public required {{arm.Type.Name}} Value { get; init; }
                }
                """);
        }

        yield return parse($$"""
            /// <summary>A <see cref="{{type.Name}}"/> shape this protocol version does not define.</summary>
            public sealed class {{type.Name}}Unknown : {{type.Name}}
            {
                /// <summary>The payload exactly as it arrived.</summary>
                public required JsonElement Raw { get; init; }
            }
            """);

        var probes = new StringBuilder();
        foreach (var arm in type.Arms)
        {
            var test = string.Join(" && ", arm.RequiredKeys.Select(k => $"root.TryGetProperty(\"{k}\", out _)"));
            probes.Append($$"""
                        if ({{(test.Length == 0 ? "true" : test)}})
                        {
                            return new {{type.Name}}{{arm.Type.Name}} { Value = AcpJson.Read<{{arm.Type.Name}}>(root, options) };
                        }

            """);
        }

        var writes = new StringBuilder();
        foreach (var arm in type.Arms)
        {
            writes.Append($$"""
                            case {{type.Name}}{{arm.Type.Name}} typed:
                                JsonSerializer.Serialize(writer, typed.Value, options.GetTypeInfo(typeof({{arm.Type.Name}})));
                                break;

            """);
        }

        yield return parse($$"""
            /// <summary>
            /// Resolves <see cref="{{type.Name}}"/> by which required keys are present, since
            /// nothing in the payload names the variant.
            /// </summary>
            internal sealed class {{converter}} : JsonConverter<{{type.Name}}>
            {
                /// <inheritdoc/>
                public override {{type.Name}} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    var root = document.RootElement;

            {{probes}}        return new {{type.Name}}Unknown { Raw = root.Clone() };
                }

                /// <inheritdoc/>
                public override void Write(Utf8JsonWriter writer, {{type.Name}} value, JsonSerializerOptions options)
                {
                    switch (value)
                    {
            {{writes}}            case {{type.Name}}Unknown unknown:
                                unknown.Raw.WriteTo(writer);
                                break;
                            default:
                                writer.WriteNullValue();
                                break;
                    }
                }
            }
            """);
    }

    /// <summary>A union over JSON token kinds, such as a string or a number.</summary>
    public static IEnumerable<MemberDeclarationSyntax> EmitValue(
        ValueUnionType type,
        string contextName,
        Func<string, MemberDeclarationSyntax> parse)
    {
        var converter = type.Name + "Converter";
        var accessors = new StringBuilder();

        foreach (var arm in type.Arms)
        {
            var name = Identifier(arm.Name);
            accessors.Append($$"""

                    /// <summary>Read this value as <c>{{CSharpEmitter.EscapeText(arm.Name)}}</c>, when that is what it holds.</summary>
                    public bool TryGet{{name}}(JsonSerializerOptions options, out {{arm.Name}} value) =>
                        AcpJson.TryRead(Raw, options, out value);

                    /// <summary>Read this value as <c>{{CSharpEmitter.EscapeText(arm.Name)}}</c> using this protocol version's contracts.</summary>
                    public bool TryGet{{name}}(out {{arm.Name}} value) =>
                        AcpJson.TryRead(Raw, {{contextName}}.Default.Options, out value);

            """);
        }

        yield return parse($$"""
            {{CSharpEmitter.DocsText(type.Documentation)}}[JsonConverter(typeof({{converter}}))]
            public readonly struct {{type.Name}}
            {
                /// <summary>Wrap a raw payload.</summary>
                public {{type.Name}}(JsonElement raw) => Raw = raw;

                /// <summary>The value exactly as it arrived.</summary>
                public JsonElement Raw { get; }

                /// <summary>Whether the value is absent.</summary>
                public bool IsNull => Raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
            {{accessors}}}
            """);

        yield return parse($$"""
            /// <summary>Reads and writes <see cref="{{type.Name}}"/> verbatim.</summary>
            internal sealed class {{converter}} : JsonConverter<{{type.Name}}>
            {
                /// <inheritdoc/>
                public override {{type.Name}} Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                {
                    using var document = JsonDocument.ParseValue(ref reader);
                    return new {{type.Name}}(document.RootElement.Clone());
                }

                /// <inheritdoc/>
                public override void Write(Utf8JsonWriter writer, {{type.Name}} value, JsonSerializerOptions options) =>
                    value.Raw.WriteTo(writer);
            }
            """);
    }

    private static string Camel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>An arm's type name reduced to something usable in a method name.</summary>
    private static string Identifier(string typeName)
    {
        var cleaned = typeName.Replace("[]", "Array").Replace(".", string.Empty);
        return cleaned.Length == 0 ? cleaned : char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }
}
