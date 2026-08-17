using AcpKit.Generator.Analysis;
using AcpKit.Generator.Emit;
using AcpKit.Generator.Verify;
using Microsoft.CodeAnalysis;
using AcpKit.Generator.Model;
using AcpKit.Generator.Schema;

namespace AcpKit.Generator;

internal static class Program
{
    private static readonly (ProtocolLine Line, SchemaVariant Variant)[] AllSets =
    [
        (ProtocolLine.V1, SchemaVariant.Stable),
        (ProtocolLine.V1, SchemaVariant.Unstable),
        (ProtocolLine.V2, SchemaVariant.Stable),
        (ProtocolLine.V2, SchemaVariant.Unstable),
    ];

    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault() ?? "help";
        var repoRoot = FindRepoRoot();

        return command switch
        {
            "inspect" => Inspect(repoRoot, Selected(args)),
            "show" => Show(repoRoot, args.Skip(1).ToArray()),
            "emit" => EmitAndVerify(repoRoot, Selected(args)),
            "generate" => Generate(repoRoot, Selected(args)),
            "help" or "--help" or "-h" => Help(),
            _ => Unknown(command),
        };
    }

    /// <summary>
    /// Report what the analysis makes of each schema, without emitting anything.
    /// </summary>
    /// <remarks>
    /// The generator's first job is to prove it understands the input. An unclassified
    /// definition here means the emitter would have produced a client missing that piece of
    /// the protocol, so this exits non-zero rather than pressing on.
    /// </remarks>
    private static int Inspect(string repoRoot, IReadOnlyList<(ProtocolLine Line, SchemaVariant Variant)> sets)
    {
        var failed = false;

        foreach (var (line, variant) in sets)
        {
            var directory = Path.Combine(repoRoot, "schema", line.ToString().ToLowerInvariant(), variant.ToString().ToLowerInvariant());
            var schema = SchemaSet.Load(directory, line, variant);

            var builder = new ModelBuilder(schema);
            var plan = builder.Build($"AcpKit.Protocol.{line}", ContextName(variant));

            Console.WriteLine();
            Console.WriteLine($"  {schema.Describe()}  {schema.Version}  (protocolVersion {schema.ProtocolVersion})");

            var counts = plan.Types
                .GroupBy(t => t switch
                {
                    AliasType => "alias",
                    OpenEnumType => "open enum",
                    ObjectType => "object",
                    UnionType => "union",
                    ShapeUnionType => "shape union",
                    ValueUnionType => "value union",
                    _ => "unknown",
                })
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            foreach (var group in counts)
            {
                Console.WriteLine($"    {group.Count(),4}  {group.Key}");
            }

            Console.WriteLine($"    {plan.Methods.Count,4}  methods");

            var threeState = plan.Types.OfType<ObjectType>()
                .SelectMany(o => o.Properties)
                .Count(p => p.ThreeState);
            var defaultOnError = plan.Types.OfType<ObjectType>()
                .SelectMany(o => o.Properties)
                .Count(p => p.DefaultOnError);
            var skipInvalid = plan.Types.OfType<ObjectType>()
                .SelectMany(o => o.Properties)
                .Count(p => p.SkipInvalidItems);

            Console.WriteLine($"    {threeState,4}  properties with v2 upsert semantics");
            Console.WriteLine($"    {defaultOnError,4}  properties with x-deserialize-default-on-error");
            Console.WriteLine($"    {skipInvalid,4}  properties with x-deserialize-skip-invalid-items");

            foreach (var note in builder.Flattened)
            {
                Console.WriteLine($"    note: {note}");
            }

            foreach (var problem in schema.MethodTableDisagreements())
            {
                Console.WriteLine($"    method table: {problem}");
                failed = true;
            }

            if (builder.Unclassified.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"    {builder.Unclassified.Count} definition(s) the analysis could not classify:");
                foreach (var item in builder.Unclassified)
                {
                    Console.WriteLine($"      {item}");
                }

                failed = true;
            }

            schema.Dispose();
        }

        Console.WriteLine();
        return failed ? 1 : 0;
    }

    /// <summary>
    /// Render each schema to C# and compile the result, reporting anything the compiler
    /// objects to. Nothing is written to disk yet — the point is the oracle, not the output.
    /// </summary>
    private static int EmitAndVerify(string repoRoot, IReadOnlyList<(ProtocolLine Line, SchemaVariant Variant)> sets)
    {
        var verifier = new CompileVerifier();
        var failed = false;

        foreach (var (line, variant) in sets)
        {
            var directory = Path.Combine(repoRoot, "schema", line.ToString().ToLowerInvariant(), variant.ToString().ToLowerInvariant());
            var schema = SchemaSet.Load(directory, line, variant);
            var suffix = variant == SchemaVariant.Unstable ? ".Unstable" : string.Empty;
            var plan = new ModelBuilder(schema).Build($"AcpKit.Protocol.{line}{suffix}", ContextName(variant));

            var rendered = new CSharpEmitter(plan).Render();
            var lines = rendered.Count(c => c == '\n');
            if (Environment.GetEnvironmentVariable("ACPKIT_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                File.WriteAllText(Path.Combine(dump, $"{line}-{variant}.g.cs".ToLowerInvariant()), rendered);
            }

            var diagnostics = verifier.Verify($"AcpKit.Protocol.{line}{suffix}", [($"{schema.Describe()}/Protocol.g.cs", rendered)]);

            Console.WriteLine();
            Console.WriteLine($"  {schema.Describe()}  {lines} lines emitted");

            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

            if (errors.Count == 0 && warnings.Count == 0)
            {
                Console.WriteLine("    compiles clean");
            }

            foreach (var diagnostic in errors.Concat(warnings).Take(15))
            {
                var span = diagnostic.Location.GetLineSpan();
                Console.WriteLine($"    {diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Id} (line {span.StartLinePosition.Line + 1}): {diagnostic.GetMessage()}");
                failed = true;
            }

            if (errors.Count + warnings.Count > 15)
            {
                Console.WriteLine($"    ... and {errors.Count + warnings.Count - 15} more");
            }

            schema.Dispose();
        }

        Console.WriteLine();
        return failed ? 1 : 0;
    }

    /// <summary>
    /// Write the generated sources into their projects.
    /// </summary>
    /// <remarks>
    /// The files are checked in, so a schema bump arrives as a reviewable diff rather than as
    /// an opaque rebuild. Building those projects is also the authoritative verification:
    /// System.Text.Json's source generator runs there and completes the serialization context,
    /// which an in-memory compilation cannot do.
    /// </remarks>
    private static int Generate(string repoRoot, IReadOnlyList<(ProtocolLine Line, SchemaVariant Variant)> sets)
    {
        foreach (var (line, variant) in sets)
        {
            var directory = Path.Combine(repoRoot, "schema", line.ToString().ToLowerInvariant(), variant.ToString().ToLowerInvariant());
            var schema = SchemaSet.Load(directory, line, variant);
            var suffix = variant == SchemaVariant.Unstable ? ".Unstable" : string.Empty;
            var @namespace = $"AcpKit.Protocol.{line}{suffix}";
            var plan = new ModelBuilder(schema).Build(@namespace, ContextName(variant));

            var project = Path.Combine(repoRoot, "src", $"AcpKit.Protocol.{line}", "Generated");
            Directory.CreateDirectory(project);

            var stem = variant == SchemaVariant.Unstable ? "Unstable" : "Protocol";
            Write(repoRoot, Path.Combine(project, $"{stem}.g.cs"), new CSharpEmitter(plan).Render());

            foreach (var (role, owner) in new[] { ("Client", MethodOwner.Client), ("Agent", MethodOwner.Agent) })
            {
                var roleProject = Path.Combine(repoRoot, "src", $"AcpKit.{role}.{line}", "Generated");
                Directory.CreateDirectory(roleProject);
                Write(repoRoot, Path.Combine(roleProject, $"{stem}.g.cs"), ConnectionEmitter.Render(plan, owner));
            }

            schema.Dispose();
        }

        Console.WriteLine();
        return 0;
    }

    private static void Write(string repoRoot, string path, string content)
    {
        File.WriteAllText(path, content);
        Console.WriteLine($"  {Path.GetRelativePath(repoRoot, path),-58} {content.Count(c => c == '\n'),6} lines");
    }

    /// <summary>Print how one named definition classified, for spot-checking the analysis.</summary>
    private static int Show(string repoRoot, string[] names)
    {
        if (names.Length == 0)
        {
            Console.Error.WriteLine("show needs at least one definition name.");
            return 2;
        }

        var directory = Path.Combine(repoRoot, "schema", "v2", "stable");
        var schema = SchemaSet.Load(directory, ProtocolLine.V2, SchemaVariant.Stable);
        var plan = new ModelBuilder(schema).Build("AcpKit.Protocol.V2", "AcpJsonContext");

        foreach (var name in names)
        {
            var type = plan.Types.FirstOrDefault(t => t.Name == name);
            Console.WriteLine();
            switch (type)
            {
                case null:
                    Console.WriteLine($"  {name}: not emitted");
                    break;
                case UnionType union:
                    Console.WriteLine($"  {name}: union on \"{union.DiscriminatorJsonName}\", {union.Variants.Count} variants, {union.BaseProperties.Count} shared properties");
                    foreach (var v in union.Variants.Take(8))
                    {
                        Console.WriteLine($"      \"{v.DiscriminatorValue}\" -> {v.PayloadType.Name}");
                    }

                    break;
                case ShapeUnionType shape:
                    Console.WriteLine($"  {name}: untagged union, {shape.Arms.Count} arms");
                    foreach (var a in shape.Arms)
                    {
                        Console.WriteLine($"      {a.Type.Name} identified by [{string.Join(", ", a.RequiredKeys)}]");
                    }

                    break;
                case ValueUnionType value:
                    Console.WriteLine($"  {name}: value union over [{string.Join(" | ", value.Arms.Select(a => a.Name))}]{(value.AllowsNull ? " | null" : string.Empty)}");
                    break;
                case OpenEnumType e:
                    Console.WriteLine($"  {name}: open enum, {e.Members.Count} known values: {string.Join(", ", e.Members.Select(m => m.WireValue))}");
                    break;
                case AliasType alias:
                    Console.WriteLine($"  {name}: alias over {alias.Underlying.Name}");
                    break;
                case ObjectType o:
                    Console.WriteLine($"  {name}: object, {o.Properties.Count} properties");
                    foreach (var prop in o.Properties)
                    {
                        var flags = new List<string>();
                        if (prop.Required) flags.Add("required");
                        if (prop.ThreeState) flags.Add("patch");
                        if (prop.DefaultOnError) flags.Add("default-on-error");
                        if (prop.SkipInvalidItems) flags.Add("skip-invalid");
                        Console.WriteLine($"      {prop.CsName,-22} {prop.Type.Name,-28} {string.Join(" ", flags)}");
                    }

                    break;
            }
        }

        schema.Dispose();
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// The serialization context's name for a variant. Stable and unstable share an assembly,
    /// and System.Text.Json's generator names its output files after the context class, so the
    /// two must differ or the generator aborts on a duplicate hint name.
    /// </summary>
    private static string ContextName(SchemaVariant variant) =>
        variant == SchemaVariant.Unstable ? "AcpUnstableJsonContext" : "AcpJsonContext";

    private static IReadOnlyList<(ProtocolLine, SchemaVariant)> Selected(string[] args)
    {
        var lines = new List<ProtocolLine>();
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--line")
            {
                lines.Add(args[i + 1].Equals("v2", StringComparison.OrdinalIgnoreCase) ? ProtocolLine.V2 : ProtocolLine.V1);
            }
        }

        return lines.Count == 0
            ? AllSets
            : AllSets.Where(s => lines.Contains(s.Line)).ToList();
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "schema")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find the repository root (no schema/ directory above the binary).");
    }

    private static int Help()
    {
        Console.WriteLine("""
            acpkit-generate

              inspect [--line v1] [--line v2]
                  Report how each vendored schema classifies, and fail if any definition
                  cannot be classified or the method table disagrees with the schema.

              show <TypeName>...
                  Print how named definitions classified, for spot-checking the analysis.

              emit [--line v1] [--line v2]
                  Render each schema to C# and compile the result, failing on any
                  diagnostic. Nothing is written to disk.

              generate [--line v1] [--line v2]
                  Write the generated sources into src/AcpKit.Protocol.V*/Generated/.
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Try 'help'.");
        return 2;
    }
}
