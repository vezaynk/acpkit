using System;
using System.Collections.Generic;
using System.Text;

namespace AcpKit.Generator
{
    /// <summary>
    /// Turning schema text into C# source text. Everything the generator writes into a
    /// literal, an identifier, or a doc comment goes through here, so that a schema
    /// description containing a quote, a backslash, or an angle bracket cannot produce
    /// source that fails to compile.
    /// </summary>
    public static class SymbolText
    {
        /// <summary>
        /// A C# string literal for <paramref name="value"/>, escaped and quoted.
        /// </summary>
        public static string QuoteLiteral(string? value)
        {
            if (value is null)
            {
                return "null";
            }

            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (char.IsControl(c))
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// XML-escape a fragment destined for a doc comment.
        /// </summary>
        public static string EscapeXml(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value!
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        /// <summary>
        /// A schema description rendered as the body lines of a <c>&lt;summary&gt;</c>, one
        /// entry per output line, already XML-escaped and without the leading <c>///</c>.
        /// Blank input answers an empty list so callers can skip the block entirely.
        /// </summary>
        public static IReadOnlyList<string> SummaryLines(string? description)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return Array.Empty<string>();
            }

            var lines = description!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var result = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                result.Add(EscapeXml(line.TrimEnd()));
            }

            // Trim leading and trailing blank lines; interior ones carry paragraph structure.
            var start = 0;
            var end = result.Count - 1;
            while (start <= end && result[start].Length == 0) start++;
            while (end >= start && result[end].Length == 0) end--;

            return result.GetRange(start, Math.Max(0, end - start + 1));
        }

        /// <summary>
        /// C# keywords that cannot be used bare as identifiers. A schema property named
        /// <c>params</c> or <c>ref</c> becomes <c>@params</c> / <c>@ref</c>.
        /// </summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
            "virtual", "void", "volatile", "while",
        };

        /// <summary>
        /// <paramref name="name"/> as a usable C# identifier, verbatim-escaped if it collides
        /// with a keyword.
        /// </summary>
        public static string Identifier(string name) => Keywords.Contains(name) ? "@" + name : name;
    }
}
