using System.Linq;
using System.Text.Json.Nodes;

namespace AcpKit.Generator
{
    /// <summary>
    /// Resolves C# property types from JSON schema definitions
    /// </summary>
    public class PropertyTypeResolver
    {
        private readonly JsonObject allDefinitions;

        public PropertyTypeResolver(JsonObject allDefinitions)
        {
            this.allDefinitions = allDefinitions;
        }

        /// <summary>
        /// Get C# type for a JSON schema property
        /// </summary>
        public string GetPropertyType(JsonObject property, string? propName = null)
        {
            // Special case: _meta property is always a dictionary
            if (propName == "_meta")
            {
                return "Dictionary<string, object>";
            }

            // Handle $ref first
            var refValue = property.Str("$ref");
            if (!string.IsNullOrEmpty(refValue))
            {
                var refName = refValue.Split('/').Last();
                return NamingHelper.ConvertNameToClass(refName);
            }

            // Handle allOf with references
            var allOf = property.Arr("allOf");
            if (allOf != null)
            {
                foreach (var item in allOf)
                {
                    var itemRef = (item as JsonObject).Str("$ref");
                    if (!string.IsNullOrEmpty(itemRef))
                    {
                        var refName = itemRef.Split('/').Last();
                        return NamingHelper.ConvertNameToClass(refName);
                    }
                }
            }

            // Handle type array (e.g., ["string", "null"], ["integer", "null"], ["array", "null"])
            var typeToken = property.Node("type");
            var isNullable = false;
            string? typeString = null;

            if (typeToken != null)
            {
                if (typeToken is JsonArray typeArray)
                {
                    isNullable = typeArray.Any(t => t.AsString() == "null");
                    var nonNullTypes = typeArray.Select(t => t.AsString()).Where(t => t != "null").ToList();

                    // Handle array type in type array (e.g., ["array", "null"])
                    if (nonNullTypes.Count > 0 && nonNullTypes[0] == "array")
                    {
                        var items = property.Obj("items");
                        var itemType = items != null ? GetPropertyType(items) : "object";
                        var result = $"{itemType}[]";
                        // For nullable array, don't add ? since arrays are reference types
                        return result;
                    }

                    if (nonNullTypes.Count == 1)
                    {
                        typeString = nonNullTypes[0];
                    }
                }
                else if (typeToken.AsString() == "array")
                {
                    // Handle simple array type
                    var items = property.Obj("items");
                    var itemType = items != null ? GetPropertyType(items) : "object";
                    return $"{itemType}[]";
                }
                else if (typeToken.AsString() is string simpleType)
                {
                    typeString = simpleType;
                }
            }

            // Handle enum
            if (property.Node("enum") != null)
            {
                return "string";
            }

            // Handle typed values
            if (!string.IsNullOrEmpty(typeString))
            {
                string mappedType;

                // Check for format hints on integer types
                if (typeString == "integer" && property.Str("format") is string format)
                {
                    mappedType = format switch
                    {
                        "uint16" => "ushort",
                        "uint32" => "uint",
                        "uint64" => "ulong",
                        "int16" => "short",
                        "int32" => "int",
                        "int64" => "long",
                        _ => "int"
                    };
                }
                else
                {
                    mappedType = TypeMapper.GetTypeName(typeString);
                }

                if (isNullable && typeString == "object")
                {
                    return "object";
                }

                // Add ? for nullable value types only
                if (isNullable && TypeMapper.IsValueType(mappedType))
                {
                    return mappedType + "?";
                }

                return mappedType;
            }

            // Handle anyOf (union types)
            var anyOf = property.Arr("anyOf");
            if (anyOf != null)
            {
                // Check if this is just a nullable reference pattern: anyOf: [{ $ref }, { type: null }]
                if (anyOf.Count == 2)
                {
                    var hasRef = false;
                    var hasNull = false;
                    string? refType = null;

                    foreach (var item in anyOf)
                    {
                        if (item is not JsonObject itemObj)
                            continue;

                        var itemRef = itemObj.Str("$ref");
                        if (!string.IsNullOrEmpty(itemRef))
                        {
                            hasRef = true;
                            var refName = itemRef.Split('/').Last();
                            refType = NamingHelper.ConvertNameToClass(refName);
                        }
                        else if (itemObj.Str("type") == "null")
                        {
                            hasNull = true;
                        }
                    }

                    // If it's a ref + null pattern, return the nullable reference type
                    if (hasRef && hasNull && !string.IsNullOrEmpty(refType))
                    {
                        return refType;
                    }
                }

                // Otherwise, return object for complex union types
                return "object";
            }

            // Handle oneOf (discriminated unions) - return object without nullable annotation
            if (property.Node("oneOf") != null)
            {
                return "object";
            }

            // Default to object (no nullable annotation)
            return "object";
        }
    }
}
