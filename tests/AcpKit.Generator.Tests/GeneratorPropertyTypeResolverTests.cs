using AcpKit.Generator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json.Nodes;

namespace AcpKit.Generator.Tests
{
    [TestClass]
    public class PropertyTypeResolverTests
    {
        private static PropertyTypeResolver CreateResolver()
        {
            return new PropertyTypeResolver(new JsonObject());
        }

        [TestMethod]
        public void GetPropertyType_ExtractsClassNameFromRef()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""$ref"": ""#/$defs/MyClass"" }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("MyClass", result);
        }

        [TestMethod]
        public void GetPropertyType_WrapsArrayItemsInArray()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": ""array"", ""items"": { ""type"": ""string"" } }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("string[]", result);
        }

        [TestMethod]
        public void GetPropertyType_DefaultsToObjectArrayForArraysWithoutItems()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": ""array"" }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("object[]", result);
        }

        [TestMethod]
        public void GetPropertyType_DoesNotAddNullableToString()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": [""string"", ""null""] }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("string", result);
        }

        [TestMethod]
        public void GetPropertyType_ReturnsStringForEnumType()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": ""string"", ""enum"": [""value1"", ""value2""] }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("string", result);
        }

        [TestMethod]
        public void GetPropertyType_ConvertsPrimitiveTypes()
        {
            var resolver = CreateResolver();

            Assert.AreEqual("string", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": ""string"" }")));
            Assert.AreEqual("int", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": ""integer"" }")));
            Assert.AreEqual("bool", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": ""boolean"" }")));
        }

        [TestMethod]
        public void GetPropertyType_ReturnsObjectForAnyOfUnion()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""anyOf"": [ { ""type"": ""string"" }, { ""type"": ""number"" } ] }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("object", result);
        }

        [TestMethod]
        public void GetPropertyType_ReturnsObjectForOneOf()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""oneOf"": [ { ""type"": ""string"" }, { ""type"": ""number"" } ] }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("object", result);
        }

        [TestMethod]
        public void GetPropertyType_HandlesNullableValueTypeFormats()
        {
            var resolver = CreateResolver();

            Assert.AreEqual("uint?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""integer"", ""null""], ""format"": ""uint32"" }")));
            Assert.AreEqual("ulong?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""integer"", ""null""], ""format"": ""uint64"" }")));
            Assert.AreEqual("ushort?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""integer"", ""null""], ""format"": ""uint16"" }")));
            Assert.AreEqual("int?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""integer"", ""null""], ""format"": ""int32"" }")));
            Assert.AreEqual("long?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""integer"", ""null""], ""format"": ""int64"" }")));
            Assert.AreEqual("short?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""integer"", ""null""], ""format"": ""int16"" }")));
            Assert.AreEqual("int?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""integer"", ""null""] }")));
            Assert.AreEqual("bool?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""boolean"", ""null""] }")));
            Assert.AreEqual("double?", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""number"", ""null""] }")));
        }

        [TestMethod]
        public void GetPropertyType_HandlesNullableReferenceTypes()
        {
            var resolver = CreateResolver();

            Assert.AreEqual("string", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""string"", ""null""] }")));
            Assert.AreEqual("object", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""object"", ""null""] }")));
            Assert.AreEqual("object[]", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": [""array"", ""null""] }")));
        }

        [TestMethod]
        public void GetPropertyType_HandlesAnyOfRefPlusNullPatterns()
        {
            var resolver = CreateResolver();

            Assert.AreEqual("Annotations", resolver.GetPropertyType(Json.ParseObject(@"{ ""anyOf"": [ { ""$ref"": ""#/$defs/Annotations"" }, { ""type"": ""null"" } ] }")));
            Assert.AreEqual("Implementation", resolver.GetPropertyType(Json.ParseObject(@"{ ""anyOf"": [ { ""type"": ""null"" }, { ""$ref"": ""#/$defs/Implementation"" } ] }")));
            Assert.AreEqual("SessionModeState", resolver.GetPropertyType(Json.ParseObject(@"{ ""anyOf"": [ { ""$ref"": ""#/$defs/SessionModeState"" }, { ""type"": ""null"" } ] }")));
            Assert.AreEqual("TerminalExitStatus", resolver.GetPropertyType(Json.ParseObject(@"{ ""anyOf"": [ { ""$ref"": ""#/$defs/TerminalExitStatus"" }, { ""type"": ""null"" } ] }")));
        }

        [TestMethod]
        public void GetPropertyType_HandlesComplexAnyOfPatterns()
        {
            var resolver = CreateResolver();

            Assert.AreEqual("object", resolver.GetPropertyType(Json.ParseObject(@"{ ""anyOf"": [ { ""$ref"": ""#/$defs/TypeA"" }, { ""$ref"": ""#/$defs/TypeB"" }, { ""$ref"": ""#/$defs/TypeC"" } ] }")));
            Assert.AreEqual("object", resolver.GetPropertyType(Json.ParseObject(@"{ ""anyOf"": [ { ""allOf"": [ { ""$ref"": ""#/$defs/ResponseA"" } ] }, { ""allOf"": [ { ""$ref"": ""#/$defs/ResponseB"" } ] } ] }")));
        }
        [TestMethod]
        public void GetPropertyType_HandleAllOfWithRefExtraction()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""allOf"": [ { ""$ref"": ""#/$defs/MyClass"" } ] }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("MyClass", result);
        }

        [TestMethod]
        public void GetPropertyType_HandlesNestedArrayItems()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("string[][]", result);
        }

        [TestMethod]
        public void GetPropertyType_HandlesNullableArrayType()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": [""array"", ""null""], ""items"": { ""type"": ""integer"" } }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("int[]", result);
        }

        [TestMethod]
        public void GetPropertyType_HandlesPrimitiveArrayTypes()
        {
            var resolver = CreateResolver();

            Assert.AreEqual("string[]", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": ""array"", ""items"": { ""type"": ""string"" } }")));
            Assert.AreEqual("bool[]", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": ""array"", ""items"": { ""type"": ""boolean"" } }")));
            Assert.AreEqual("double[]", resolver.GetPropertyType(Json.ParseObject(@"{ ""type"": ""array"", ""items"": { ""type"": ""number"" } }")));
        }

        [TestMethod]
        public void GetPropertyType_HandlesArrayOfReferences()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": ""array"", ""items"": { ""$ref"": ""#/$defs/MyType"" } }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("MyType[]", result);
        }

        [TestMethod]
        public void GetPropertyType_HandlesTypeArrayWithOnlyNull()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": [""null""] }");

            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("object", result);
        }

        [TestMethod]
        public void GetPropertyType_ReturnsObjectForMultipleTypeOptions()
        {
            var resolver = CreateResolver();
            // When there are multiple non-null types, it may not be able to resolve all
            var property = Json.ParseObject(@"{ ""type"": [""string"", ""integer"", ""null""] }");

            var result = resolver.GetPropertyType(property);
            // Should return object for ambiguous cases
            Assert.AreEqual("object", result);
        }

        [TestMethod]
        public void GetPropertyType_ReturnsDictionaryForMetaProperty()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": [""object"", ""null""], ""additionalProperties"": true }");

            var result = resolver.GetPropertyType(property, "_meta");
            Assert.AreEqual("Dictionary<string, object>", result);
        }

        [TestMethod]
        public void GetPropertyType_ReturnsDictionaryForMetaPropertyRegardlessOfDefinition()
        {
            var resolver = CreateResolver();
            // Even with different property definitions, _meta should always return Dictionary
            var property = Json.ParseObject(@"{ ""type"": ""string"" }");

            var result = resolver.GetPropertyType(property, "_meta");
            Assert.AreEqual("Dictionary<string, object>", result);
        }

        [TestMethod]
        public void GetPropertyType_DoesNotReturnDictionaryForOtherProperties()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": [""object"", ""null""], ""additionalProperties"": true }");

            // Without _meta property name, should not return Dictionary
            var result = resolver.GetPropertyType(property);
            Assert.AreEqual("object", result);
        }

        [TestMethod]
        public void GetPropertyType_MetaPropertyWithNullableType()
        {
            var resolver = CreateResolver();
            var property = Json.ParseObject(@"{ ""type"": [""object"", ""null""] }");

            var result = resolver.GetPropertyType(property, "_meta");
            Assert.AreEqual("Dictionary<string, object>", result);
        }
    }
}
