using AcpKit.Generator.Model;
using AcpKit.Generator.Schema;

namespace AcpKit.Generator.Analysis;

/// <summary>
/// Turns a <see cref="SchemaSet"/> into an <see cref="EmitPlan"/>.
/// </summary>
/// <remarks>
/// The whole of the generator's protocol understanding lives here. Nothing downstream reads
/// the schema again; the emitter works only from the model, which is why the model is a
/// closed hierarchy and why an unrecognised construct has to be reported rather than quietly
/// rendered as <c>object</c>.
/// </remarks>
internal sealed class ModelBuilder(SchemaSet schema)
{
    private readonly List<string> _unsupported = [];
    private readonly List<string> _flattened = [];

    /// <summary>
    /// Definitions the builder could not classify. A non-empty list means the schema grew a
    /// construct this generator has never seen, which is a generation failure rather than
    /// something to paper over — the alternative is emitting a client that silently drops
    /// protocol surface.
    /// </summary>
    public IReadOnlyList<string> Unclassified => _unsupported;

    /// <summary>
    /// Definitions whose union arms were folded into the parent object. Reported rather than
    /// silent: it is a deliberate loss of an exclusivity constraint, and a reader deserves to
    /// know which types it applies to.
    /// </summary>
    public IReadOnlyList<string> Flattened => _flattened;

    public EmitPlan Build(string @namespace)
    {
        var types = new List<EmittedType>();

        foreach (var (name, definition) in schema.Definitions.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (definition.Flag("x-docs-ignore"))
            {
                continue;
            }

            if (Classify(name, definition) is { } emitted)
            {
                types.Add(emitted);
            }
        }

        var methods = schema.Methods
            .Select(m => new MethodConstant(Naming.Constant(m.Key), m.Path, m.Side.ToString()))
            .ToList();

        return new EmitPlan(@namespace, schema.Version, schema.ProtocolVersion, types, methods);
    }

    private EmittedType? Classify(string name, SchemaNode definition)
    {
        var csName = Naming.Type(name);
        var documentation = definition.Text("description");

        // A closed `enum` list. Still emitted open: v2 requires unknown values to round-trip,
        // and v1 carries the same x-deserialize hints, so the same latent bug.
        if (definition["enum"].IsArray)
        {
            var members = definition.Items("enum")
                .Select(item => item.AsScalarText())
                .Where(text => text is not null)
                .Select(text => new EnumMember(Naming.EnumMember(text!), text!, null))
                .ToList();

            return members.Count > 0
                ? new OpenEnumType(csName, documentation, members)
                : Unsupported(name, "an enum with no readable members");
        }

        var variantNodes = definition.Items("anyOf").Concat(definition.Items("oneOf")).ToList();
        var (typeNames, _) = definition.TypeNames();
        var hasOwnProperties = definition["properties"].IsObject;

        if (variantNodes.Count > 0)
        {
            return ClassifyUnion(name, csName, documentation, definition, variantNodes, hasOwnProperties);
        }

        if (typeNames is ["object"] || hasOwnProperties)
        {
            return new ObjectType(csName, documentation, ReadProperties(definition));
        }

        // A bare primitive that upstream nonetheless gave a name to. That naming is the whole
        // signal: SessionId and ToolCallId are both strings on the wire and must not be
        // interchangeable in code.
        if (typeNames.Count == 1 && Primitive(typeNames[0], definition) is { } underlying)
        {
            return new AliasType(csName, documentation, underlying);
        }

        // A definition carrying nothing but a description is JSON Schema's "any value". ACP
        // uses it for the extension escape hatch — ExtRequest, ExtNotification, ExtResponse —
        // which by construction has no shape the generator could know.
        if (typeNames.Count == 0 && !definition["properties"].Exists && !definition["items"].Exists)
        {
            return new AliasType(csName, documentation, TypeRef.Object);
        }

        return Unsupported(name, $"type [{string.Join(", ", typeNames)}] with no properties, enum, or union");
    }

    private EmittedType? ClassifyUnion(
        string name,
        string csName,
        string? documentation,
        SchemaNode definition,
        List<SchemaNode> variantNodes,
        bool hasOwnProperties)
    {
        // Arms carrying a `const` marker: a discriminated union. The discriminator property
        // is whichever property holds the const, and every arm must agree on it — a union
        // whose arms disagree is not a union this generator can resolve deterministically.
        var discriminated = new List<(string Property, string Value, SchemaNode Node)>();
        var constOnly = new List<string>();
        var plainArms = new List<SchemaNode>();

        foreach (var arm in variantNodes)
        {
            var marker = FindConstMarker(arm);
            if (marker is { } found)
            {
                discriminated.Add((found.Property, found.Value, arm));
            }
            else if (arm["const"].Exists && arm["const"].AsScalarText() is { } bare)
            {
                constOnly.Add(bare);
            }
            else
            {
                plainArms.Add(arm);
            }
        }

        // Every arm is a bare `const` string, optionally with a free-string fallback arm:
        // that is how ACP spells an open enum whose known values are enumerated.
        if (constOnly.Count > 0 && discriminated.Count == 0)
        {
            var members = constOnly
                .Select(value => new EnumMember(Naming.EnumMember(value), value, null))
                .ToList();
            return new OpenEnumType(csName, documentation, members);
        }

        if (discriminated.Count > 0)
        {
            var properties = discriminated.Select(d => d.Property).Distinct(StringComparer.Ordinal).ToList();
            if (properties.Count > 1)
            {
                return Unsupported(name, $"union arms disagree on the discriminator: {string.Join(", ", properties)}");
            }

            var variants = new List<UnionVariant>();
            foreach (var (_, value, node) in discriminated)
            {
                var payload = node.AllOfRefName() ?? node.RefName();
                var payloadType = payload is not null
                    ? new TypeRef(Naming.Type(payload), false)
                    : new TypeRef(csName + Naming.EnumMember(value), false);

                variants.Add(new UnionVariant(
                    csName + Naming.EnumMember(value),
                    value,
                    payloadType,
                    node.Text("description")));
            }

            // Own properties on a discriminated union are shared by every variant, not a
            // reason to stop being a union: DiffChange keys on `operation` while `path` and
            // `fileType` apply to all six operations.
            var baseProperties = hasOwnProperties
                ? ReadProperties(definition).Where(p => p.JsonName != properties[0]).ToList()
                : (IReadOnlyList<PropertyModel>)[];

            return new UnionType(csName, documentation, properties[0], variants, baseProperties);
        }

        // Arms with no discriminator, on a definition that has fields of its own: the arms
        // each contribute a further set of fields rather than selecting between shapes.
        // ElicitationFormMode carries requestedSchema plus one scope variant. Flattening them
        // in, all optional, is faithful to what appears on the wire; what is lost is only the
        // constraint that exactly one arm applies, which no C# shape could enforce at
        // deserialization time anyway.
        if (hasOwnProperties)
        {
            var merged = ReadProperties(definition).ToList();
            var seen = merged.Select(p => p.JsonName).ToHashSet(StringComparer.Ordinal);

            foreach (var arm in variantNodes)
            {
                var armDefinition = schema.Resolve(arm.AllOfRefName() ?? arm.RefName());
                foreach (var property in ReadProperties(armDefinition))
                {
                    if (seen.Add(property.JsonName))
                    {
                        merged.Add(property with { Required = false, Nullable = true });
                    }
                }
            }

            _flattened.Add($"{name}: {variantNodes.Count} union arm(s) flattened into the object");
            return new ObjectType(csName, documentation, merged);
        }

        // No discriminator anywhere. Arms that merge an object $ref are told apart by which
        // required keys they carry; arms that are primitives or arrays are told apart by JSON
        // token kind. Both are decided here, at generation time, rather than by trying each
        // candidate at runtime and catching the failure.
        var shapeArms = new List<ShapeUnionArm>();
        foreach (var arm in plainArms)
        {
            var merged = arm.AllOfRefName();
            if (merged is null)
            {
                shapeArms.Clear();
                break;
            }

            var target = schema.Resolve(merged);
            var keys = target.Strings("required");
            shapeArms.Add(new ShapeUnionArm(
                new TypeRef(Naming.Type(merged), false),
                keys,
                arm.Text("description")));
        }

        if (shapeArms.Count > 0 && shapeArms.Count == plainArms.Count)
        {
            // One arm and nothing to choose between: the union is a rename of its only member.
            if (shapeArms.Count == 1)
            {
                return new AliasType(csName, documentation, shapeArms[0].Type);
            }

            var distinguishing = shapeArms
                .Select(a => string.Join(",", a.RequiredKeys.Order(StringComparer.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .Count();

            if (distinguishing != shapeArms.Count)
            {
                return Unsupported(name, "an untagged union whose arms share the same required keys");
            }

            return new ShapeUnionType(csName, documentation, shapeArms);
        }

        var arms = new List<TypeRef>();
        var allowsNull = false;

        foreach (var arm in plainArms)
        {
            var (armTypes, armNull) = arm.TypeNames();
            allowsNull |= armNull;

            if (arm.RefName() is { } referenced)
            {
                arms.Add(new TypeRef(Naming.Type(referenced), false));
                continue;
            }

            if (armTypes.Count == 1 && Primitive(armTypes[0], arm) is { } primitive)
            {
                arms.Add(primitive);
                continue;
            }

            // SessionConfigSelectOptions is a list of options or a list of groups. Both arms
            // are arrays, so they are told apart by element type rather than token kind.
            if (armTypes is ["array"] && arm["items"].Exists)
            {
                arms.Add(ResolveType(string.Empty, arm["items"]).Array());
                continue;
            }

            if (armTypes.Count == 0 && armNull)
            {
                continue;
            }

            return Unsupported(name, "a union arm that is neither a $ref, an array, a primitive, nor null");
        }

        if (arms.Count == 0)
        {
            return Unsupported(name, "a union with no resolvable arms");
        }

        var distinct = arms.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count();
        if (distinct != arms.Count)
        {
            return Unsupported(name, "a union with two arms of the same C# type");
        }

        return new ValueUnionType(csName, documentation, arms, allowsNull);
    }

    /// <summary>
    /// The property carrying a <c>const</c> that selects a union arm.
    /// </summary>
    /// <remarks>
    /// The marker is inline on the arm in most of ACP, but not always: <c>AuthMethod</c>
    /// declares its arm as nothing but a title and an <c>allOf</c> merge, and the <c>type</c>
    /// discriminator lives on the merged-in <c>AuthMethodAgent</c>. Following the merge is
    /// what tells a discriminated union apart from a shapeless one.
    /// </remarks>
    private (string Property, string Value)? FindConstMarker(SchemaNode arm)
    {
        if (FindConstIn(arm) is { } inline)
        {
            return inline;
        }

        var merged = arm.AllOfRefName() ?? arm.RefName();
        return merged is null ? null : FindConstIn(schema.Resolve(merged));
    }

    private static (string Property, string Value)? FindConstIn(SchemaNode node)
    {
        foreach (var (propertyName, property) in node.Fields("properties"))
        {
            if (property["const"].AsScalarText() is { } value)
            {
                return (propertyName, value);
            }
        }

        return null;
    }

    private IReadOnlyList<PropertyModel> ReadProperties(SchemaNode definition)
    {
        var required = definition.Strings("required").ToHashSet(StringComparer.Ordinal);
        var properties = new List<PropertyModel>();

        foreach (var (jsonName, node) in definition.Fields("properties"))
        {
            var allowsNull = node.AdmitsNull();
            var isRequired = required.Contains(jsonName);

            // Optional *and* nullable is exactly the ACP v2 upsert signature: omitted means
            // leave unchanged, null means clear, a value means replace. Three instructions,
            // which a plain nullable property cannot express.
            var threeState = schema.Line == ProtocolLine.V2 && !isRequired && allowsNull;

            properties.Add(new PropertyModel(
                JsonName: jsonName,
                CsName: Naming.Property(jsonName),
                Type: ResolveType(jsonName, node),
                Required: isRequired,
                Nullable: allowsNull || !isRequired,
                ThreeState: threeState,
                DefaultOnError: node.Flag("x-deserialize-default-on-error"),
                SkipInvalidItems: node.Flag("x-deserialize-skip-invalid-items"),
                Documentation: node.Text("description")));
        }

        return properties;
    }

    private TypeRef ResolveType(string jsonName, SchemaNode node)
    {
        // _meta is the protocol's open extension slot. It is kept as raw JSON so that unknown
        // metadata survives a round trip verbatim, which v2 requires of anything proxying or
        // replaying traffic.
        if (jsonName == "_meta")
        {
            return TypeRef.Object;
        }

        if (node.RefName() is { } referenced)
        {
            return new TypeRef(Naming.Type(referenced), false);
        }

        if (node.AllOfRefName() is { } merged)
        {
            return new TypeRef(Naming.Type(merged), false);
        }

        var (typeNames, _) = node.TypeNames();

        if (typeNames.Contains("array"))
        {
            var items = node["items"];
            var element = items.Exists ? ResolveType(string.Empty, items) : TypeRef.Object;
            return element.Array();
        }

        if (typeNames.Count == 1 && Primitive(typeNames[0], node) is { } primitive)
        {
            return primitive;
        }

        // anyOf of [$ref, null] is how the schema spells an optional reference.
        foreach (var arm in node.Items("anyOf"))
        {
            if (arm.RefName() is { } armRef)
            {
                return new TypeRef(Naming.Type(armRef), false);
            }
        }

        return TypeRef.Object;
    }

    /// <summary>The C# type for a JSON primitive, honouring integer format hints.</summary>
    private static TypeRef? Primitive(string jsonType, SchemaNode node) => jsonType switch
    {
        "string" => TypeRef.String,
        "boolean" => new TypeRef("bool", true),
        "number" => new TypeRef("double", true),
        "integer" => new TypeRef(node.Text("format") switch
        {
            "uint16" => "ushort",
            "uint32" => "uint",
            "uint64" => "ulong",
            "int16" => "short",
            "int32" => "int",
            "int64" => "long",
            _ => "int",
        }, true),
        _ => null,
    };

    private EmittedType? Unsupported(string name, string why)
    {
        _unsupported.Add($"{name}: {why}");
        return null;
    }
}
