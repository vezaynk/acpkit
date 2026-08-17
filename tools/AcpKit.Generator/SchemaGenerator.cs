using System.Text.Json.Nodes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AcpKit.Generator
{
    /// <summary>
    /// Generates Schema.cs from schema.json
    /// </summary>
    public class SchemaGenerator
    {
        private JsonObject definitions = new JsonObject();
        private PropertyTypeResolver typeResolver = null!;
        private DiscriminatorAnalyzer discriminatorAnalyzer = null!;

        /// <summary>
        /// Generate Schema.cs from schema.json
        /// </summary>
        public string Generate(string schemaJsonPath, string versionFilePath, string targetNamespace = "dotacp.protocol")
        {
            var schema = Json.ParseObjectFile(schemaJsonPath);

            definitions = schema.Obj("$defs") ?? new JsonObject();
            typeResolver = new PropertyTypeResolver(definitions);
            discriminatorAnalyzer = new DiscriminatorAnalyzer(definitions);

            var sb = new StringBuilder();

            // Generate header
            sb.AppendLineLf("// Generated from schema/schema.json. Do not edit by hand.");

            if (File.Exists(versionFilePath))
            {
                var gitRef = File.ReadAllText(versionFilePath).Trim();
                if (!string.IsNullOrEmpty(gitRef))
                {
                    sb.AppendLineLf($"// Schema ref: {gitRef}");
                }
            }

            sb.AppendLineLf();
            sb.AppendLineLf("#pragma warning disable CS1591");
            sb.AppendLineLf();

            // Generate using statements
            sb.AppendLineLf("using Newtonsoft.Json;");
            sb.AppendLineLf("using System;");
            sb.AppendLineLf("using System.Collections.Generic;");
            sb.AppendLineLf();

            // Start namespace
            sb.AppendLineLf($"namespace {targetNamespace}");
            sb.AppendLineLf("{");

            // Separate definitions by type
            var typeAliases = new List<string>();
            var enumDefinitions = new List<string>();
            var recordClasses = new List<string>();

            foreach (var defProp in definitions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var defName = defProp.Key;
                if (defProp.Value is not JsonObject def)
                    continue;

                if (IsDocsIgnored(def))
                    continue;

                var classCode = GenerateModelClass(defName, def);

                // Check type of generated code
                var hasEnum = classCode.Contains("public enum ") || classCode.Contains("public abstract enum ");
                var hasClass = classCode.Contains("public class ") || classCode.Contains("public abstract class ");

                if (hasEnum)
                {
                    enumDefinitions.Add(classCode);
                }
                else if (hasClass)
                {
                    recordClasses.Add(classCode);
                }
                else if (classCode.Contains("IEquatable<"))
                {
                    typeAliases.Add(classCode);
                }
                else
                {
                    recordClasses.Add(classCode);
                }
            }

            // Add type aliases first
            if (typeAliases.Count > 0)
            {
                sb.AppendLineLf("    // Type aliases");
                sb.AppendLineLf();
                foreach (var alias in typeAliases)
                {
                    sb.Append(IndentCode(alias, 1));
                    sb.AppendLineLf();
                }
            }

            // Add enums next
            if (enumDefinitions.Count > 0)
            {
                sb.AppendLineLf("    // Enums for string-based enum-like types");
                sb.AppendLineLf();
                foreach (var enumDef in enumDefinitions)
                {
                    sb.Append(IndentCode(enumDef, 1));
                    sb.AppendLineLf();
                }
            }

            // Then add class definitions
            sb.AppendLineLf("    // Generated model classes from ACP schema");
            sb.AppendLineLf();
            foreach (var recordClass in recordClasses)
            {
                sb.Append(IndentCode(recordClass, 1));
                sb.AppendLineLf();
            }

            // Close namespace
            sb.AppendLineLf("}");
            sb.AppendLineLf();
            sb.AppendLineLf("#pragma warning restore CS1591");

            return sb.ToString();
        }

        private static bool IsDocsIgnored(JsonObject definition) => definition.Flag("x-docs-ignore");

        private string GenerateModelClass(string name, JsonObject definition)
        {
            var modelBuilder = new ModelClassBuilder(
                name,
                definition,
                definitions,
                typeResolver!,
                discriminatorAnalyzer!
            );

            return modelBuilder.Generate();
        }

        private string IndentCode(string code, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 4);
            var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();

            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sb.Append(indent);
                }
                sb.AppendLineLf(line);
            }

            return sb.ToString();
        }
    }
}
