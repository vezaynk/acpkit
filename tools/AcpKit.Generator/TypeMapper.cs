using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace AcpKit.Generator
{
    /// <summary>
    /// Helper for mapping JSON schema types to C# types
    /// </summary>
    public static class TypeMapper
    {
        /// <summary>
        /// Map JSON type name to C# type name
        /// </summary>
        public static string GetTypeName(string jsonType)
        {
            switch (jsonType)
            {
                case "string": return "string";
                case "number": return "double";
                case "integer": return "int";
                case "boolean": return "bool";
                case "object": return "object";
                case "array": return "object[]";
                case "null": return "object";
                default: return "object";
            }
        }

        /// <summary>
        /// Convert default value to C# syntax
        /// </summary>
        public static string? ConvertDefaultValue(object? value, string csType)
        {
            if (value == null)
            {
                return null;
            }

            // Unwrap a JsonNode down to the CLR value it carries.
            var actualValue = value is JsonNode node ? Json.ToClrValue(node) : value;

            // Handle bool types
            if (csType.StartsWith("bool"))
            {
                if (actualValue is bool boolValue)
                {
                    return boolValue ? "true" : "false";
                }
                // Try to convert other bool representations
                if (bool.TryParse(actualValue?.ToString() ?? "", out var result))
                {
                    return result ? "true" : "false";
                }
            }

            // Handle int types
            if (csType.StartsWith("int"))
            {
                if (actualValue is long longValue)
                {
                    return longValue.ToString();
                }
                if (actualValue is int intValue)
                {
                    return intValue.ToString();
                }
                // Try to parse
                if (int.TryParse(actualValue?.ToString() ?? "", out var intResult))
                {
                    return intResult.ToString();
                }
            }

            // Handle double types
            if (csType.StartsWith("double"))
            {
                if (actualValue is double doubleValue)
                {
                    return doubleValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            // Handle array types (check before string types since string[] starts with "string")
            if (csType.EndsWith("[]"))
            {
                return $"new {csType.TrimEnd('[', ']')}[0]";
            }

            // Handle Dictionary types
            if (csType.StartsWith("Dictionary<"))
            {
                return $"new {csType}()";
            }

            // Handle string types
            if (csType.StartsWith("string"))
            {
                if (actualValue is string stringValue)
                {
                    return SymbolText.QuoteLiteral(stringValue);
                }
            }

            return null;
        }

        /// <summary>
        /// Check if a C# type is a value type (needs ? for nullable)
        /// </summary>
        public static bool IsValueType(string csType)
        {
            var valueTypes = new HashSet<string>
            {
                "int", "bool", "double", "uint", "ushort", "ulong",
                "short", "long", "byte", "sbyte", "float", "decimal", "char"
            };

            return valueTypes.Contains(csType);
        }

        /// <summary>
        /// Type aliases that are value types (structs)
        /// </summary>
        public static HashSet<string> TypeAliases = new HashSet<string>
        {
            "PermissionOptionId",
            "ProtocolVersion",
            "SessionConfigGroupId",
            "SessionConfigId",
            "SessionConfigValueId",
            "SessionId",
            "SessionModeId",
            "ToolCallId",
            "RequestId",
            "SessionConfigSelectOptions"
        };

        /// <summary>
        /// Enum types (value types)
        /// </summary>
        public static HashSet<string> EnumTypes = new HashSet<string>
        {
            "PermissionOptionKind",
            "PlanEntryPriority",
            "PlanEntryStatus",
            "StopReason",
            "ToolCallStatus",
            "ToolKind",
            "SessionConfigOptionCategory",
            "ErrorCode",
            "ElicitationStringType",
            "NesDiagnosticSeverity",
            "TextDocumentSyncKind",
            "LlmProtocol",
            "NesTriggerKind",
        };

        /// <summary>
        /// Check if a type is a reference type (class, string, object, List, etc.)
        /// </summary>
        public static bool IsReferenceType(string csType)
        {
            if (IsValueType(csType))
            {
                return false;
            }

            if (TypeAliases.Contains(csType))
            {
                return false;
            }

            if (EnumTypes.Contains(csType))
            {
                return false;
            }

            return true;
        }
    }
}
