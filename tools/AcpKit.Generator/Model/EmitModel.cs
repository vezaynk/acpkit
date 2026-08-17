namespace AcpKit.Generator.Model;

/// <summary>
/// A reference to a C# type in generated code.
/// </summary>
/// <param name="Name">The rendered type name, e.g. <c>string</c>, <c>SessionId</c>, <c>ContentBlock[]</c>.</param>
/// <param name="IsValueType">
/// Whether the type is a struct. Decides whether nullability renders as <c>T?</c> meaning
/// <see cref="Nullable{T}"/> or as an annotation on a reference type.
/// </param>
internal sealed record TypeRef(string Name, bool IsValueType)
{
    public static TypeRef String { get; } = new("string", false);

    public static TypeRef Object { get; } = new("System.Text.Json.JsonElement", true);

    /// <summary>An array of this type.</summary>
    public TypeRef Array() => new($"{Name}[]", false);

    /// <summary>How the type is written when a value may be absent.</summary>
    public string Render(bool nullable) => nullable ? $"{Name}?" : Name;
}

/// <summary>One member of an open string enum.</summary>
/// <param name="CsName">The C# member name.</param>
/// <param name="WireValue">The exact string that appears on the wire.</param>
internal sealed record EnumMember(string CsName, string WireValue, string? Documentation);

/// <summary>
/// One property of a generated object.
/// </summary>
/// <param name="ThreeState">
/// True when the property carries ACP v2 upsert semantics, where <em>omitted</em>,
/// <em>null</em>, and <em>a value</em> are three distinct instructions — leave unchanged,
/// clear, and replace. A plain nullable property collapses the first two and loses the
/// distinction the protocol is built on, so these render as <c>Patch&lt;T&gt;</c>.
/// </param>
/// <param name="DefaultOnError">
/// From <c>x-deserialize-default-on-error</c>: a malformed or unrecognised value falls back
/// to the default instead of failing the whole message. This is how ACP expresses forward
/// compatibility, and honouring it is what lets an old client survive a newer agent.
/// </param>
/// <param name="SkipInvalidItems">
/// From <c>x-deserialize-skip-invalid-items</c>: drop array entries that will not parse
/// rather than rejecting the array.
/// </param>
internal sealed record PropertyModel(
    string JsonName,
    string CsName,
    TypeRef Type,
    bool Required,
    bool Nullable,
    bool ThreeState,
    bool DefaultOnError,
    bool SkipInvalidItems,
    string? Documentation);

/// <summary>One arm of a discriminated union.</summary>
/// <param name="DiscriminatorValue">The <c>const</c> that selects this arm.</param>
/// <param name="PayloadType">The type carrying the arm's fields.</param>
/// <param name="Inline">
/// True when the payload type <em>is</em> the variant: it is used by this arm alone, and its
/// name already matches. Such a payload derives from the union directly instead of being
/// wrapped, which removes a pointless <c>.Value</c> hop from the public API.
/// </param>
internal sealed record UnionVariant(
    string CsName,
    string DiscriminatorValue,
    TypeRef PayloadType,
    string? Documentation,
    bool Inline = false);

/// <summary>
/// A type the generator intends to emit.
/// </summary>
/// <remarks>
/// Closed on purpose. Every consumer switches over this hierarchy, and with
/// warnings-as-errors a switch that forgets an arm fails the build (CS8509). That is the
/// structural replacement for deciding what was produced by searching the generated text for
/// <c>"public enum "</c>, which is both fragile and unable to notice a case it has never seen.
/// </remarks>
internal abstract record EmittedType(string Name, string? Documentation);

/// <summary>
/// A semantic wrapper over a primitive: <c>SessionId</c>, <c>ToolCallId</c>, <c>ProtocolVersion</c>.
/// </summary>
/// <remarks>
/// Emitted as a readonly struct rather than a bare <c>string</c> so that a session id cannot
/// be passed where a tool-call id belongs. The protocol distinguishes them; the types should too.
/// </remarks>
internal sealed record AliasType(string Name, string? Documentation, TypeRef Underlying)
    : EmittedType(Name, Documentation);

/// <summary>
/// A string enum that accepts values it has never heard of.
/// </summary>
/// <remarks>
/// ACP v2 requires every enum-like string to round-trip unknown values: those beginning with
/// <c>_</c> are vendor extensions, and the rest are reserved for future protocol versions.
/// A closed C# <c>enum</c> cannot represent either, so these are emitted as readonly structs
/// over <c>string</c> with well-known members as statics.
/// </remarks>
internal sealed record OpenEnumType(string Name, string? Documentation, IReadOnlyList<EnumMember> Members)
    : EmittedType(Name, Documentation);

/// <summary>An object with named properties.</summary>
/// <param name="UnionBase">
/// The union this object is a variant of, when it derives from one directly. See
/// <see cref="UnionVariant.Inline"/>.
/// </param>
/// <param name="DiscriminatorValue">The discriminator value selecting it, when <paramref name="UnionBase"/> is set.</param>
internal sealed record ObjectType(
    string Name,
    string? Documentation,
    IReadOnlyList<PropertyModel> Properties,
    string? UnionBase = null,
    string? DiscriminatorValue = null)
    : EmittedType(Name, Documentation);

/// <summary>
/// A union selected by a <c>const</c> discriminator property, such as <c>ContentBlock</c>
/// keyed on <c>type</c> or <c>SessionUpdate</c> keyed on <c>sessionUpdate</c>.
/// </summary>
/// <param name="BaseProperties">
/// Properties the union declares on itself, shared by every variant. <c>DiffChange</c> is
/// the clearest case: <c>path</c> and <c>fileType</c> apply whatever the <c>operation</c>.
/// </param>
internal sealed record UnionType(
    string Name,
    string? Documentation,
    string DiscriminatorJsonName,
    IReadOnlyList<UnionVariant> Variants,
    IReadOnlyList<PropertyModel> BaseProperties)
    : EmittedType(Name, Documentation);

/// <summary>
/// A union with no discriminator at all, whose arms are told apart by which required
/// properties are present. <c>EmbeddedResourceResource</c> is the canonical case: a text
/// resource carries <c>text</c>, a blob resource carries <c>blob</c>, and nothing marks
/// which is which.
/// </summary>
/// <remarks>
/// The required-key sets are computed here, at generation time, so the emitted converter is
/// a literal chain of key probes. The reflective SDKs instead construct each candidate in
/// turn and catch the failure, which is both quadratic on the hot path and silently wrong
/// whenever two arms have overlapping shapes.
/// </remarks>
internal sealed record ShapeUnionType(
    string Name,
    string? Documentation,
    IReadOnlyList<ShapeUnionArm> Arms)
    : EmittedType(Name, Documentation);

/// <summary>One arm of an untagged union, with the keys that identify it.</summary>
internal sealed record ShapeUnionArm(TypeRef Type, IReadOnlyList<string> RequiredKeys, string? Documentation);

/// <summary>
/// A union over JSON value kinds rather than a discriminator — <c>RequestId</c> being a
/// string or a number, for instance. Resolved by token kind, which is unambiguous.
/// </summary>
internal sealed record ValueUnionType(
    string Name,
    string? Documentation,
    IReadOnlyList<TypeRef> Arms,
    bool AllowsNull)
    : EmittedType(Name, Documentation);

/// <summary>Everything one (line, variant) contributes to a generated assembly.</summary>
/// <param name="ContextName">
/// The serialization context's class name. Distinct per variant, because System.Text.Json's
/// source generator derives its output filenames from the class name alone — two contexts
/// called the same thing in one assembly collide on hint names and the generator aborts,
/// taking every contract with it.
/// </param>
internal sealed record EmitPlan(
    string Namespace,
    string ContextName,
    string SchemaVersion,
    int ProtocolVersion,
    IReadOnlyList<EmittedType> Types,
    IReadOnlyList<MethodConstant> Methods);

/// <summary>A method name, destined for a constant on the generated method table.</summary>
internal sealed record MethodConstant(string CsName, string Path, string Side);
