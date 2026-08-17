using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace AcpKit.Generator
{
    /// <summary>
    /// Generates Meta.cs from meta.json
    /// </summary>
    public class MetaGenerator
    {
        /// <summary>
        /// Generate Meta.cs from meta.json
        /// </summary>
        public string Generate(string metaJsonPath, string versionFilePath, string targetNamespace = "dotacp.protocol")
        {
            var meta = Json.ParseObjectFile(metaJsonPath);

            var sb = new StringBuilder();

            // Generate header
            sb.AppendLineLf("// Generated from schema/meta.json. Do not edit by hand.");

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
            sb.AppendLineLf($"namespace {targetNamespace}");
            sb.AppendLineLf("{");

            // Generate ProtocolMeta class
            sb.AppendLineLf("    /// <summary>");
            sb.AppendLineLf("    /// Protocol metadata");
            sb.AppendLineLf("    /// </summary>");
            sb.AppendLineLf("    public static class ProtocolMeta");
            sb.AppendLineLf("    {");

            // Add protocol version
            var version = (ushort)(meta.Int("version") ?? 1);
            sb.AppendLineLf("        /// <summary>");
            sb.AppendLineLf("        /// ACP Protocol Version");
            sb.AppendLineLf("        /// </summary>");
            sb.AppendLineLf($"        public const ushort Version = {version};");

            sb.AppendLineLf("    }");
            sb.AppendLineLf();

            // Generate AgentMethods class
            sb.AppendLineLf("    /// <summary>");
            sb.AppendLineLf("    /// Methods that agents handle");
            sb.AppendLineLf("    /// </summary>");
            sb.AppendLineLf("    public static class AgentMethods");
            sb.AppendLineLf("    {");

            AppendMethodConstants(sb, meta.Obj("agentMethods"));

            sb.AppendLineLf("    }");
            sb.AppendLineLf();

            // Generate ClientMethods class
            sb.AppendLineLf("    /// <summary>");
            sb.AppendLineLf("    /// Methods that clients handle");
            sb.AppendLineLf("    /// </summary>");
            sb.AppendLineLf("    public static class ClientMethods");
            sb.AppendLineLf("    {");

            AppendMethodConstants(sb, meta.Obj("clientMethods"));

            sb.AppendLineLf("    }");
            sb.AppendLineLf("}");
            sb.AppendLineLf();
            sb.AppendLineLf("#pragma warning restore CS1591");

            return sb.ToString();
        }

        private static void AppendMethodConstants(StringBuilder sb, JsonObject? methods)
        {
            if (methods is null)
            {
                return;
            }

            foreach (var prop in methods.OrderBy(p => p.Key, System.StringComparer.Ordinal))
            {
                var constName = NamingHelper.ConvertToPascalCase(prop.Key);
                var methodPath = prop.Value.AsStringLoose() ?? string.Empty;
                sb.AppendLineLf($"        public const string {constName} = {SymbolText.QuoteLiteral(methodPath)};");
            }
        }
    }
}
