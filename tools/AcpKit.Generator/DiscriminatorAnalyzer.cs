using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AcpKit.Generator
{
    /// <summary>
    /// Analyzes JSON schema definitions for discriminated unions
    /// </summary>
    public class DiscriminatorAnalyzer
    {
        private readonly JsonObject definitions;
        private readonly Dictionary<string, int> refCounts;

        public Dictionary<string, DiscriminatorBaseInfo> BaseInfo { get; } = new Dictionary<string, DiscriminatorBaseInfo>();
        public Dictionary<string, DiscriminatorDerivedInfo> DerivedInfo { get; } = new Dictionary<string, DiscriminatorDerivedInfo>();
        public Dictionary<string, List<DiscriminatorVariant>> VariantClasses { get; } = new Dictionary<string, List<DiscriminatorVariant>>();
        public HashSet<string> AbstractBases { get; } = new HashSet<string>();
        public Dictionary<string, string> ChildToAbstractBase { get; } = new Dictionary<string, string>();

        /// <summary>
        /// anyOf/oneOf unions where no variant has a const discriminator in JSON (e.g. only titles).
        /// Like Python's plain union (A | B). We generate an abstract base with ObjectUnionConverter.
        /// </summary>
        public Dictionary<string, List<string>> UnionWithoutDiscriminator { get; } = new Dictionary<string, List<string>>();

        public DiscriminatorAnalyzer(JsonObject definitions)
        {
            this.definitions = definitions;
            this.refCounts = GetDefinitionRefCounts(definitions);
            AnalyzeDiscriminators();
            AnalyzeDiscriminatorsInAnyOfVariants();
            AnalyzeAbstractBases();
        }

        private void AnalyzeDiscriminators()
        {
            foreach (var defProp in definitions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var defName = defProp.Key;
                if (defProp.Value is not JsonObject def)
                    continue;

                // Check if this has a discriminator and oneOf
                var discriminator = def.Obj("discriminator");
                var oneOf = def.Arr("oneOf");

                if (discriminator == null || oneOf == null)
                    continue;

                var propertyName = discriminator.Str("propertyName");
                if (string.IsNullOrEmpty(propertyName))
                    continue;

                var baseClassName = NamingHelper.ConvertNameToClass(defName);
                var discInfo = NamingHelper.GetDiscriminatorPropertyInfo(baseClassName, propertyName);

                // Collect variants
                var variants = new List<(string? RefName, string ConstValue, JsonObject Item)>();

                foreach (var item in oneOf)
                {
                    if (item is not JsonObject itemObj)
                        continue;

                    // Check for $ref, else the first $ref merged in via allOf
                    var itemRef = itemObj.Str("$ref");
                    string? refName = !string.IsNullOrEmpty(itemRef)
                        ? itemRef.Split('/').Last()
                        : FirstAllOfRef(itemObj);

                    // Get const value
                    string? constValue = null;
                    if (itemObj.Obj("properties").Obj(propertyName) is JsonObject discProp)
                    {
                        constValue = discProp.Node("const").AsStringLoose();
                    }

                    if (constValue == null)
                        continue;

                    variants.Add((refName, constValue, itemObj));
                }

                if (variants.Count == 0)
                    continue;

                // Count refs in this base
                var refCountsInBase = new Dictionary<string, int>();
                foreach (var variant in variants)
                {
                    if (!string.IsNullOrEmpty(variant.RefName))
                    {
                        if (!refCountsInBase.ContainsKey(variant.RefName))
                        {
                            refCountsInBase[variant.RefName] = 0;
                        }
                        refCountsInBase[variant.RefName]++;
                    }
                }

                var mapping = new Dictionary<string, string>();

                foreach (var variant in variants)
                {
                    var refName = variant.RefName;
                    var constValue = variant.ConstValue ?? "";
                    var variantItem = variant.Item;

                    var globalRefCount = 0;
                    var localRefCount = 0;

                    if (!string.IsNullOrEmpty(refName))
                    {
                        if (refCounts.ContainsKey(refName))
                        {
                            globalRefCount = refCounts[refName];
                        }
                        if (refCountsInBase.ContainsKey(refName))
                        {
                            localRefCount = refCountsInBase[refName];
                        }
                    }

                    // Use direct inheritance if ref is only used once globally and once locally
                    var useDirectInheritance = !string.IsNullOrEmpty(refName) &&
                                               globalRefCount == 1 &&
                                               localRefCount == 1;

                    if (useDirectInheritance)
                    {
                        DerivedInfo[refName!] = new DiscriminatorDerivedInfo
                        {
                            BaseName = defName,
                            PropertyName = propertyName,
                            PropertyCsName = discInfo.CsName,
                            PropertyJsonName = discInfo.JsonName,
                            DiscriminatorValue = constValue,
                            IsAbstract = false
                        };

                        mapping[constValue] = NamingHelper.ConvertNameToClass(refName!);
                    }
                    else
                    {
                        // Create wrapper variant class
                        var variantClassName = NamingHelper.ConvertNameToClass(defName) +
                                             NamingHelper.ConvertPropertyName(constValue);

                        if (!VariantClasses.ContainsKey(defName))
                        {
                            VariantClasses[defName] = new List<DiscriminatorVariant>();
                        }

                        // Get variant definition
                        var variantDefinition = variantItem;
                        if (!string.IsNullOrEmpty(refName) && definitions.Obj(refName) is JsonObject referencedDef)
                        {
                            variantDefinition = referencedDef;
                        }

                        VariantClasses[defName].Add(new DiscriminatorVariant
                        {
                            ClassName = variantClassName,
                            BaseClassName = NamingHelper.ConvertNameToClass(defName),
                            DiscriminatorPropertyName = propertyName,
                            DiscriminatorPropertyCsName = discInfo.CsName,
                            DiscriminatorPropertyJsonName = discInfo.JsonName,
                            DiscriminatorValue = constValue,
                            Definition = variantDefinition,
                            Description = variantItem.Str("description")
                        });

                        mapping[constValue] = variantClassName;
                    }
                }

                BaseInfo[defName] = new DiscriminatorBaseInfo
                {
                    PropertyName = propertyName,
                    PropertyCsName = discInfo.CsName,
                    PropertyJsonName = discInfo.JsonName,
                    Mapping = mapping
                };
            }
        }

        private void AnalyzeDiscriminatorsInAnyOfVariants()
        {
            foreach (var defProp in definitions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var defName = defProp.Key;
                if (defProp.Value is not JsonObject def)
                    continue;

                // Skip if already has explicit discriminator
                if (def.Node("discriminator") != null || def.Node("oneOf") != null)
                    continue;

                // Check for anyOf with discriminator in variants
                var anyOf = def.Arr("anyOf");
                if (anyOf == null || anyOf.Count == 0)
                    continue;

                // Only process anyOf that has complex types (allOf + $ref), not primitive unions
                // Primitive unions like ErrorCode (anyOf with const integer values) and 
                // RequestId (anyOf with null/integer/string types) should not be treated as discriminated unions
                bool hasComplexVariant = anyOf.Objects().Any(itemObj => FirstAllOfRef(itemObj) != null);

                // Skip primitive union types
                if (!hasComplexVariant)
                    continue;

                // Try to detect discriminator property from first variant
                string? discriminatorProperty = null;
                var variants = new List<(string? RefName, string ConstValue, string? Title, JsonObject Item, bool HadConstInProperties)>();

                foreach (var item in anyOf)
                {
                    if (item is not JsonObject itemObj)
                        continue;

                    string? constValue = null;
                    string? title = itemObj.Str("title");
                    bool hadConstInProperties = false;

                    // Get $ref from allOf
                    string? refName = FirstAllOfRef(itemObj);

                    // Look for const value in properties
                    var properties = itemObj.Obj("properties");
                    if (properties != null)
                    {
                        foreach (var prop in properties)
                        {
                            if (prop.Value is JsonObject propObj && propObj.Node("const") is JsonNode constNode)
                            {
                                discriminatorProperty = prop.Key;
                                constValue = constNode.AsStringLoose();
                                hadConstInProperties = true;
                                break;
                            }
                        }
                    }

                    // If no const value but we have a title, use title as the value (variant has no discriminator in JSON)
                    if (constValue == null && !string.IsNullOrEmpty(title))
                    {
                        if (discriminatorProperty == null)
                            discriminatorProperty = "type";
                        constValue = title;
                    }

                    if (!string.IsNullOrEmpty(constValue))
                    {
                        variants.Add((refName, constValue, title, itemObj, hadConstInProperties));
                    }
                }

                // Need at least 2 variants
                if (variants.Count < 2)
                    continue;

                // Skip if already in BaseInfo
                if (BaseInfo.ContainsKey(defName))
                    continue;

                bool anyVariantHasConst = variants.Any(v => v.HadConstInProperties);

                // No variant has a const discriminator in JSON (e.g. EmbeddedResourceResource: only titles).
                // Generate as union without discriminator (like Python A | B); do not require a "type" field.
                if (!anyVariantHasConst)
                {
                    var variantClassNames = new List<string>();
                    foreach (var variant in variants)
                    {
                        var refName = variant.RefName;
                        var constValue = variant.ConstValue ?? "";
                        var className = !string.IsNullOrEmpty(refName)
                            ? NamingHelper.ConvertNameToClass(refName)
                            : NamingHelper.ConvertNameToClass(defName) + NamingHelper.ConvertPropertyName(constValue);
                        variantClassNames.Add(className);
                        if (!string.IsNullOrEmpty(refName))
                            ChildToAbstractBase[refName] = defName;
                    }
                    UnionWithoutDiscriminator[defName] = variantClassNames;
                    continue;
                }

                if (discriminatorProperty == null)
                    continue;

                var baseClassName = NamingHelper.ConvertNameToClass(defName);
                var discInfo = NamingHelper.GetDiscriminatorPropertyInfo(baseClassName, discriminatorProperty);

                var mapping = new Dictionary<string, string>();
                var refCountsInBase = new Dictionary<string, int>();

                foreach (var variant in variants)
                {
                    if (!string.IsNullOrEmpty(variant.RefName))
                    {
                        if (!refCountsInBase.ContainsKey(variant.RefName))
                            refCountsInBase[variant.RefName] = 0;
                        refCountsInBase[variant.RefName]++;
                    }
                }

                string? defaultTypeWhenMissing = null;
                foreach (var variant in variants)
                {
                    var refName = variant.RefName;
                    var constValue = variant.ConstValue ?? "";

                    // Variants with no const in properties (title-only) are the default when discriminator is missing
                    if (!variant.HadConstInProperties && string.IsNullOrEmpty(defaultTypeWhenMissing))
                    {
                        var defaultClassName = !string.IsNullOrEmpty(refName)
                            ? NamingHelper.ConvertNameToClass(refName)
                            : baseClassName + NamingHelper.ConvertPropertyName(constValue);
                        defaultTypeWhenMissing = defaultClassName;
                    }

                    var globalRefCount = 0;
                    var localRefCount = 0;

                    if (!string.IsNullOrEmpty(refName))
                    {
                        if (refCounts.ContainsKey(refName))
                            globalRefCount = refCounts[refName];
                        if (refCountsInBase.ContainsKey(refName))
                            localRefCount = refCountsInBase[refName];
                    }

                    // Use direct inheritance if ref is only used once globally and once locally
                    var useDirectInheritance = !string.IsNullOrEmpty(refName) &&
                                               globalRefCount == 1 &&
                                               localRefCount == 1;

                    if (useDirectInheritance)
                    {
                        DerivedInfo[refName!] = new DiscriminatorDerivedInfo
                        {
                            BaseName = defName,
                            PropertyName = discriminatorProperty,
                            PropertyCsName = discInfo.CsName,
                            PropertyJsonName = discInfo.JsonName,
                            DiscriminatorValue = constValue,
                            IsAbstract = false
                        };

                        mapping[constValue] = NamingHelper.ConvertNameToClass(refName!);
                    }
                    else
                    {
                        // Create wrapper variant class
                        var variantClassName = baseClassName + NamingHelper.ConvertPropertyName(constValue);

                        if (!VariantClasses.ContainsKey(defName))
                        {
                            VariantClasses[defName] = new List<DiscriminatorVariant>();
                        }

                        // Get variant definition
                        var variantDefinition = variant.Item;
                        if (!string.IsNullOrEmpty(refName) && definitions.Obj(refName) is JsonObject referencedDef)
                        {
                            variantDefinition = referencedDef;
                        }

                        VariantClasses[defName].Add(new DiscriminatorVariant
                        {
                            ClassName = variantClassName,
                            BaseClassName = baseClassName,
                            DiscriminatorPropertyName = discriminatorProperty,
                            DiscriminatorPropertyCsName = discInfo.CsName,
                            DiscriminatorPropertyJsonName = discInfo.JsonName,
                            DiscriminatorValue = constValue,
                            Definition = variantDefinition,
                            Description = variant.Item.Str("description")
                        });

                        mapping[constValue] = variantClassName;
                    }
                }

                BaseInfo[defName] = new DiscriminatorBaseInfo
                {
                    PropertyName = discriminatorProperty,
                    PropertyCsName = discInfo.CsName,
                    PropertyJsonName = discInfo.JsonName,
                    Mapping = mapping,
                    DefaultTypeWhenDiscriminatorMissing = defaultTypeWhenMissing
                };
            }
        }

        private static Dictionary<string, int> GetDefinitionRefCounts(JsonObject defs)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var defProp in defs)
            {
                CountRefs(defProp.Value, counts);
            }

            return counts;
        }

        private static void CountRefs(JsonNode? node, Dictionary<string, int> counts)
        {
            if (node == null) return;

            if (node is JsonObject obj)
            {
                foreach (var prop in obj)
                {
                    if (prop.Key == "$ref" && prop.Value.AsStringLoose() is string refPath)
                    {
                        var refName = refPath.Split('/').Last();
                        counts.TryGetValue(refName, out var current);
                        counts[refName] = current + 1;
                    }
                    else
                    {
                        CountRefs(prop.Value, counts);
                    }
                }
            }
            else if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    CountRefs(item, counts);
                }
            }
        }

        /// <summary>
        /// The first <c>$ref</c> target named inside a variant's <c>allOf</c>, or null when the
        /// variant merges no referenced definition. This is the shape every ACP union variant
        /// uses: a <c>const</c> marker property combined with a <c>$ref</c> to the payload.
        /// </summary>
        private static string? FirstAllOfRef(JsonObject itemObj)
        {
            foreach (var allOfItem in itemObj.Arr("allOf").Objects())
            {
                var allOfRef = allOfItem.Str("$ref");
                if (!string.IsNullOrEmpty(allOfRef))
                {
                    return allOfRef.Split('/').Last();
                }
            }

            return null;
        }

        private void AnalyzeAbstractBases()
        {
            foreach (var defProp in definitions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var defName = defProp.Key;
                if (defProp.Value is not JsonObject def)
                    continue;

                // Skip if it already has a discriminator (handled by AnalyzeDiscriminators)
                if (def.Node("discriminator") != null)
                    continue;

                // Check for anyOf with allOf refs
                var anyOf = def.Arr("anyOf");
                if (anyOf == null)
                    continue;

                // Check if all items have allOf with $ref
                var childRefs = new List<string>();
                foreach (var item in anyOf)
                {
                    if (item is not JsonObject itemObj)
                        continue;

                    if (itemObj.Arr("allOf") == null)
                    {
                        childRefs.Clear();
                        break;
                    }

                    // Look for $ref in allOf
                    string? refName = FirstAllOfRef(itemObj);

                    if (string.IsNullOrEmpty(refName))
                    {
                        childRefs.Clear();
                        break;
                    }

                    childRefs.Add(refName);
                }

                // If we found valid child refs, mark this as an abstract base
                if (childRefs.Count > 0)
                {
                    AbstractBases.Add(defName);
                    foreach (var childRef in childRefs)
                    {
                        ChildToAbstractBase[childRef] = defName;
                    }
                }
            }
        }
    }
}
