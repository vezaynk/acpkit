using System.Text.Json;

namespace AcpKit.Generator.Schema;

/// <summary>
/// A read-only view over one node of a JSON Schema document.
/// </summary>
/// <remarks>
/// <para>
/// Every accessor answers a missing member with <see langword="null"/> or an empty sequence
/// rather than throwing. In a schema reader a missing key is not an error, it is an answer:
/// "the schema does not say". Making that the normal path keeps the analysis free of
/// defensive nesting.
/// </para>
/// <para>
/// This is a struct wrapping a <see cref="JsonElement"/>, so navigating a 400 KB schema
/// allocates nothing.
/// </para>
/// </remarks>
internal readonly struct SchemaNode(JsonElement element)
{
    private readonly JsonElement _element = element;

    /// <summary>The absent node. Every accessor on it answers empty.</summary>
    public static SchemaNode None => default;

    public bool Exists => _element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    public bool IsObject => _element.ValueKind == JsonValueKind.Object;

    public bool IsArray => _element.ValueKind == JsonValueKind.Array;

    public JsonElement Element => _element;

    /// <summary>The child at <paramref name="name"/>, or <see cref="None"/>.</summary>
    public SchemaNode this[string name] =>
        _element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(name, out var child)
            ? new SchemaNode(child)
            : None;

    /// <summary>The string this node carries, or null when it is not a JSON string.</summary>
    public string? AsString() => _element.ValueKind == JsonValueKind.String ? _element.GetString() : null;

    /// <summary>
    /// The scalar this node carries rendered as text — a string as itself, anything else as
    /// its JSON. Used for <c>const</c>, which is a string almost everywhere in ACP but an
    /// integer in a few places such as error codes.
    /// </summary>
    public string? AsScalarText() => _element.ValueKind switch
    {
        JsonValueKind.String => _element.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => _element.GetRawText(),
        _ => null,
    };

    public bool AsBool() => _element.ValueKind == JsonValueKind.True;

    /// <summary>The string at <paramref name="name"/>.</summary>
    public string? Text(string name) => this[name].AsString();

    /// <summary>True when <paramref name="name"/> is present and set to <c>true</c>.</summary>
    public bool Flag(string name) => this[name].AsBool();

    /// <summary>The array members at <paramref name="name"/>.</summary>
    public IEnumerable<SchemaNode> Items(string name)
    {
        var node = this[name];
        if (!node.IsArray)
        {
            yield break;
        }

        foreach (var item in node._element.EnumerateArray())
        {
            yield return new SchemaNode(item);
        }
    }

    /// <summary>The members of this node, when it is an array.</summary>
    public IEnumerable<SchemaNode> AsItems()
    {
        if (!IsArray)
        {
            yield break;
        }

        foreach (var item in _element.EnumerateArray())
        {
            yield return new SchemaNode(item);
        }
    }

    /// <summary>The properties of the object at <paramref name="name"/>, in schema order.</summary>
    public IEnumerable<(string Name, SchemaNode Value)> Fields(string name)
    {
        var node = this[name];
        if (!node.IsObject)
        {
            yield break;
        }

        foreach (var property in node._element.EnumerateObject())
        {
            yield return (property.Name, new SchemaNode(property.Value));
        }
    }

    /// <summary>The strings in the array at <paramref name="name"/>.</summary>
    public IReadOnlyList<string> Strings(string name)
    {
        var result = new List<string>();
        foreach (var item in Items(name))
        {
            if (item.AsString() is { } text)
            {
                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>
    /// The definition name this node references, or null when it is not a <c>$ref</c>.
    /// </summary>
    /// <remarks>
    /// ACP only ever refs into its own <c>$defs</c>, so the pointer is always
    /// <c>#/$defs/Name</c> and the last segment is the whole answer.
    /// </remarks>
    public string? RefName()
    {
        var pointer = Text("$ref");
        if (string.IsNullOrEmpty(pointer))
        {
            return null;
        }

        var slash = pointer.LastIndexOf('/');
        return slash < 0 ? pointer : pointer[(slash + 1)..];
    }

    /// <summary>
    /// The first <c>$ref</c> merged in through <c>allOf</c>.
    /// </summary>
    /// <remarks>
    /// This is the shape every ACP union variant uses: an object contributing a <c>const</c>
    /// discriminator, combined via <c>allOf</c> with a <c>$ref</c> to the payload type.
    /// </remarks>
    public string? AllOfRefName()
    {
        foreach (var item in Items("allOf"))
        {
            if (item.RefName() is { } name)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this node admits <c>null</c>, however the schema chooses to spell it.
    /// </summary>
    /// <remarks>
    /// There are two spellings and they are not interchangeable to a naive reader. A plain
    /// value uses <c>"type": ["string", "null"]</c>; a reference uses
    /// <c>"anyOf": [{ "$ref": ... }, { "type": "null" }]</c>, because a <c>$ref</c> cannot
    /// carry a <c>type</c> alongside it. Reading only the first spelling makes every nullable
    /// <em>reference</em> look non-nullable — which in ACP v2 silently strips upsert semantics
    /// from exactly the fields that have them, <c>ToolCallUpdate.kind</c> and
    /// <c>ToolCallUpdate.status</c> among them.
    /// </remarks>
    public bool AdmitsNull()
    {
        var (_, viaType) = TypeNames();
        if (viaType)
        {
            return true;
        }

        foreach (var arm in Items("anyOf"))
        {
            if (arm.Text("type") == "null")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The JSON types this node admits, and whether <c>null</c> is one of them.
    /// </summary>
    /// <remarks>
    /// <c>type</c> is either a string or an array of them. The nullable case matters well
    /// beyond convenience: in ACP v2 a property that is optional <em>and</em> admits null is
    /// exactly the signature of upsert patch semantics, where omitted, null, and a value are
    /// three distinct instructions.
    /// </remarks>
    public (IReadOnlyList<string> Types, bool AllowsNull) TypeNames()
    {
        var node = this["type"];

        if (node.AsString() is { } single)
        {
            return single == "null" ? ([], true) : ([single], false);
        }

        if (!node.IsArray)
        {
            return ([], false);
        }

        var types = new List<string>();
        var allowsNull = false;
        foreach (var item in node.AsItems())
        {
            var text = item.AsString();
            if (text == "null")
            {
                allowsNull = true;
            }
            else if (text is not null)
            {
                types.Add(text);
            }
        }

        return (types, allowsNull);
    }
}
