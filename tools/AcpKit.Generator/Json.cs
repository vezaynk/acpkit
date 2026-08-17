using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcpKit.Generator
{
    /// <summary>
    /// Small helpers over <see cref="JsonNode"/> that the schema readers lean on.
    /// <para>
    /// The schema is untrusted-ish input in the sense that every lookup can miss, so these
    /// all answer <see langword="null"/> rather than throwing. A missing key means "the
    /// schema does not say", which is a normal answer everywhere in this generator.
    /// </para>
    /// </summary>
    public static class Json
    {
        private static readonly JsonDocumentOptions DocumentOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static readonly JsonNodeOptions NodeOptions = new JsonNodeOptions
        {
            PropertyNameCaseInsensitive = false,
        };

        /// <summary>Parse JSON text into an object node, or throw if it is not an object.</summary>
        public static JsonObject ParseObject(string json)
        {
            var node = JsonNode.Parse(json, NodeOptions, DocumentOptions)
                ?? throw new InvalidDataException("Expected a JSON object, found null.");
            return node as JsonObject
                ?? throw new InvalidDataException($"Expected a JSON object, found {node.GetValueKind()}.");
        }

        /// <summary>Parse a file into an object node.</summary>
        public static JsonObject ParseObjectFile(string path) => ParseObject(File.ReadAllText(path));

        /// <summary>The node at <paramref name="name"/>, or null when absent.</summary>
        public static JsonNode? Node(this JsonObject? obj, string name)
        {
            if (obj is null)
            {
                return null;
            }

            return obj.TryGetPropertyValue(name, out var value) ? value : null;
        }

        /// <summary>The object at <paramref name="name"/>, or null when absent or not an object.</summary>
        public static JsonObject? Obj(this JsonObject? obj, string name) => obj.Node(name) as JsonObject;

        /// <summary>The array at <paramref name="name"/>, or null when absent or not an array.</summary>
        public static JsonArray? Arr(this JsonObject? obj, string name) => obj.Node(name) as JsonArray;

        /// <summary>The string at <paramref name="name"/>, or null when absent or not a string.</summary>
        public static string? Str(this JsonObject? obj, string name) => AsString(obj.Node(name));

        /// <summary>The boolean at <paramref name="name"/>, or null when absent or not a boolean.</summary>
        public static bool? Bool(this JsonObject? obj, string name) => obj.Node(name)?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

        /// <summary>True when <paramref name="name"/> is present and set to <c>true</c>.</summary>
        public static bool Flag(this JsonObject? obj, string name) => obj.Node(name)?.GetValueKind() == JsonValueKind.True;

        /// <summary>The string a node carries, or null when the node is not a JSON string.</summary>
        public static string? AsString(this JsonNode? node)
        {
            if (node is null || node.GetValueKind() != JsonValueKind.String)
            {
                return null;
            }

            return node.GetValue<string>();
        }

        /// <summary>
        /// The string a node carries, falling back to its JSON text for non-string nodes.
        /// Mirrors Newtonsoft's <c>JToken.ToString()</c>, which the ported code relied on.
        /// </summary>
        public static string? AsStringLoose(this JsonNode? node)
        {
            if (node is null)
            {
                return null;
            }

            return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        }

        /// <summary>Every string in an array node, skipping non-string entries.</summary>
        public static List<string> StringList(this JsonArray? array)
        {
            var result = new List<string>();
            if (array is null)
            {
                return result;
            }

            foreach (var item in array)
            {
                var text = item.AsString();
                if (text is not null)
                {
                    result.Add(text);
                }
            }

            return result;
        }

        /// <summary>Every object in an array node, skipping non-object entries.</summary>
        public static IEnumerable<JsonObject> Objects(this JsonArray? array)
        {
            if (array is null)
            {
                yield break;
            }

            foreach (var item in array)
            {
                if (item is JsonObject obj)
                {
                    yield return obj;
                }
            }
        }

        /// <summary>
        /// The CLR value behind a node: string, bool, long, double, or null. Objects and
        /// arrays answer their JSON text, which is all the callers here ever want from them.
        /// </summary>
        public static object? ToClrValue(JsonNode? node)
        {
            if (node is null)
            {
                return null;
            }

            switch (node.GetValueKind())
            {
                case JsonValueKind.String:
                    return node.GetValue<string>();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.Number:
                    return node.TryGetValue<long>(out var l) ? l : node.GetValue<double>();
                default:
                    return node.ToJsonString();
            }
        }

        /// <summary>
        /// A node detached from its parent. <see cref="JsonNode"/> forbids adding a node that
        /// already has a parent, so anything moved between documents has to be copied first.
        /// </summary>
        public static JsonNode? Detached(this JsonNode? node) => node?.DeepClone();

        private static bool TryGetValue<T>(this JsonNode node, out T value)
        {
            try
            {
                value = node.GetValue<T>();
                return true;
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
            {
                value = default!;
                return false;
            }
        }
    }
}
