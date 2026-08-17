using System.Text.Json;

namespace AcpKit.Generator.Schema;

/// <summary>Which ACP protocol line a schema belongs to.</summary>
internal enum ProtocolLine
{
    V1,
    V2,
}

/// <summary>Whether a schema is the stable surface or the unstable superset.</summary>
internal enum SchemaVariant
{
    Stable,
    Unstable,
}

/// <summary>One method the protocol defines, and which side answers it.</summary>
internal sealed record ProtocolMethod(string Key, string Path, MethodSide Side);

internal enum MethodSide
{
    Agent,
    Client,
    Protocol,
}

/// <summary>
/// The three vendored documents for one (line, variant): the schema, the method table, and
/// the upstream git ref they were taken from.
/// </summary>
internal sealed class SchemaSet
{
    private readonly JsonDocument _schema;
    private readonly JsonDocument _meta;

    private SchemaSet(ProtocolLine line, SchemaVariant variant, string version, JsonDocument schema, JsonDocument meta)
    {
        Line = line;
        Variant = variant;
        Version = version;
        _schema = schema;
        _meta = meta;

        Root = new SchemaNode(schema.RootElement);
        Definitions = Root.Fields("$defs").ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);
        Methods = ReadMethods(new SchemaNode(meta.RootElement));
        ProtocolVersion = meta.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var parsed)
            ? parsed
            : throw new InvalidDataException($"{Describe()} meta.json has no numeric \"version\".");
    }

    public ProtocolLine Line { get; }

    public SchemaVariant Variant { get; }

    /// <summary>The upstream git ref, e.g. <c>refs/tags/schema-v1.20.0</c>.</summary>
    public string Version { get; }

    /// <summary>The <c>protocolVersion</c> this line negotiates: 1 or 2.</summary>
    public int ProtocolVersion { get; }

    public SchemaNode Root { get; }

    /// <summary>Every definition under <c>$defs</c>, by name.</summary>
    public IReadOnlyDictionary<string, SchemaNode> Definitions { get; }

    /// <summary>Every method in the table, in declaration order.</summary>
    public IReadOnlyList<ProtocolMethod> Methods { get; }

    /// <summary>The definition a <c>$ref</c> names, or <see cref="SchemaNode.None"/>.</summary>
    public SchemaNode Resolve(string? definitionName) =>
        definitionName is not null && Definitions.TryGetValue(definitionName, out var node) ? node : SchemaNode.None;

    /// <summary>Load the set rooted at <paramref name="directory"/>.</summary>
    public static SchemaSet Load(string directory, ProtocolLine line, SchemaVariant variant)
    {
        var schemaPath = Path.Combine(directory, "schema.json");
        var metaPath = Path.Combine(directory, "meta.json");
        var versionPath = Path.Combine(directory, "VERSION");

        foreach (var required in new[] { schemaPath, metaPath })
        {
            if (!File.Exists(required))
            {
                throw new FileNotFoundException($"Schema set is incomplete: {required} is missing.", required);
            }
        }

        var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "(unpinned)";
        var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var meta = JsonDocument.Parse(File.ReadAllBytes(metaPath));

        return new SchemaSet(line, variant, version, schema, meta);
    }

    private static List<ProtocolMethod> ReadMethods(SchemaNode meta)
    {
        var methods = new List<ProtocolMethod>();

        foreach (var (group, side) in new[]
                 {
                     ("agentMethods", MethodSide.Agent),
                     ("clientMethods", MethodSide.Client),
                     ("protocolMethods", MethodSide.Protocol),
                 })
        {
            foreach (var (key, value) in meta.Fields(group))
            {
                if (value.AsString() is { } path)
                {
                    methods.Add(new ProtocolMethod(key, path, side));
                }
            }
        }

        return methods;
    }

    /// <summary>
    /// Cross-check the method table against the <c>x-method</c> / <c>x-side</c> annotations
    /// carried by the schema's own request definitions.
    /// </summary>
    /// <remarks>
    /// Upstream publishes the same fact twice — once in <c>meta.json</c> and once as schema
    /// annotations — and there is no reason to trust one over the other. Comparing them turns
    /// a silent upstream inconsistency into a generation failure, which is the only moment
    /// anyone would notice it.
    /// </remarks>
    public IReadOnlyList<string> MethodTableDisagreements()
    {
        var problems = new List<string>();
        // Grouped, not keyed: a method can be answered by both sides. mcp/message is, in the
        // v1 unstable surface, because MCP passthrough flows in both directions.
        var declared = Methods
            .GroupBy(m => m.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Side).ToHashSet(), StringComparer.Ordinal);
        var annotated = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, definition) in Definitions)
        {
            var method = definition.Text("x-method");
            var side = definition.Text("x-side");
            if (method is null || side is null)
            {
                continue;
            }

            // Request and response definitions both carry the annotation; one entry is enough.
            annotated.TryAdd(method, side);

            if (!declared.ContainsKey(method))
            {
                problems.Add($"{name} is annotated x-method=\"{method}\" but meta.json does not list it.");
            }
        }

        foreach (var method in Methods)
        {
            if (method.Side == MethodSide.Protocol)
            {
                // $/cancel_request has no request type of its own to annotate.
                continue;
            }

            if (!annotated.TryGetValue(method.Path, out var side))
            {
                problems.Add($"meta.json lists \"{method.Path}\" but no definition is annotated with it.");
                continue;
            }

            // "both" is the schema's own marker for a bidirectional method and agrees with any
            // table entry by definition.
            if (string.Equals(side, "both", StringComparison.Ordinal))
            {
                continue;
            }

            // Only complain when the schema names a side the table does not know about at
            // all. A bidirectional method legitimately appears under both.
            var sides = declared[method.Path];
            var annotatedSide = string.Equals(side, "agent", StringComparison.Ordinal) ? MethodSide.Agent : MethodSide.Client;
            if (!sides.Contains(annotatedSide))
            {
                problems.Add($"\"{method.Path}\" is annotated x-side=\"{side}\" but meta.json lists it only for {string.Join("/", sides)}.");
            }
        }

        return problems;
    }

    public string Describe() => $"{Line.ToString().ToLowerInvariant()}/{Variant.ToString().ToLowerInvariant()}";

    public void Dispose()
    {
        _schema.Dispose();
        _meta.Dispose();
    }
}
