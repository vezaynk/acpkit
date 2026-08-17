using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AcpKit.Generator.Verify;

/// <summary>
/// Compiles generated source and reports what the compiler thinks of it.
/// </summary>
/// <remarks>
/// <para>
/// This is the generator's primary oracle. Asserting that emitted text contains a particular
/// substring only ever proves that one substring is present; compiling proves the whole file
/// is valid C#, that every type it names resolves, that nothing is declared twice, and — under
/// the same nullable and warnings-as-errors settings the shipped projects use — that the
/// nullability annotations are coherent.
/// </para>
/// <para>
/// It runs as part of generation rather than afterwards, so bad output never reaches disk.
/// </para>
/// </remarks>
internal sealed class CompileVerifier
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> FrameworkReferences = new(LoadFrameworkReferences);

    /// <summary>
    /// Diagnostics that only System.Text.Json's own source generator can resolve.
    /// </summary>
    /// <remarks>
    /// The emitted <c>AcpJsonContext</c> is a partial class whose other half — the constructor,
    /// <c>GetTypeInfo</c>, and <c>GeneratedSerializerOptions</c> — is produced by the
    /// System.Text.Json generator during a real build. This in-memory compilation runs no
    /// analyzers, so it necessarily sees the half that is missing. Suppressing exactly these
    /// three ids keeps the fast pre-check useful without pretending to be the final word; the
    /// authoritative check is building the generated projects, where the generator does run.
    /// </remarks>
    private static readonly HashSet<string> CompletedBySourceGenerator =
        new(StringComparer.Ordinal) { "CS0534", "CS7036", "CS0260" };

    /// <summary>
    /// Compile <paramref name="sources"/> and answer every error and warning.
    /// </summary>
    /// <remarks>
    /// Warnings count. The shipped projects build with <c>TreatWarningsAsErrors</c>, so
    /// generated code that merely warns would still break the build that consumes it.
    /// </remarks>
    public IReadOnlyList<Diagnostic> Verify(string assemblyName, IReadOnlyList<(string Path, string Text)> sources)
    {
        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s.Text, path: s.Path))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            FrameworkReferences.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                // The shipped projects generate documentation files, which turns malformed doc
                // comments into diagnostics. Matching that here is the point: a schema
                // description containing a stray angle bracket must not become a downstream
                // build failure that nobody can trace back to the schema.
                xmlReferenceResolver: XmlFileResolver.Default));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Where(d => !CompletedBySourceGenerator.Contains(d.Id))
            .ToList();
    }

    /// <summary>
    /// References to the running framework, which is what the generated code targets.
    /// </summary>
    private static ImmutableArray<MetadataReference> LoadFrameworkReferences()
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (trusted.Length > 0)
        {
            foreach (var path in trusted)
            {
                builder.Add(MetadataReference.CreateFromFile(path));
            }

            AddCore(builder, trusted);
            return builder.ToImmutable();
        }

        // Fallback for hosts that do not publish the trusted-assembly list.
        foreach (var assembly in new[] { typeof(object).Assembly, typeof(System.Text.Json.JsonSerializer).Assembly })
        {
            builder.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        AddCore(builder, []);
        return builder.ToImmutable();
    }

    /// <summary>
    /// Reference AcpKit.Core, which generated code binds against for <c>Patch&lt;T&gt;</c>,
    /// <c>AcpJson</c>, and the error types.
    /// </summary>
    private static void AddCore(ImmutableArray<MetadataReference>.Builder builder, string[] alreadyReferenced)
    {
        var core = typeof(AcpKit.AcpJson).Assembly.Location;
        if (core.Length > 0 && !alreadyReferenced.Contains(core, StringComparer.Ordinal))
        {
            builder.Add(MetadataReference.CreateFromFile(core));
        }
    }
}
