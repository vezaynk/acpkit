using System.Text;

namespace AcpKit.Generator.Analysis;

/// <summary>Turning protocol names into C# names.</summary>
internal static class Naming
{
    /// <summary>
    /// A JSON property name as a C# property name.
    /// </summary>
    /// <remarks>
    /// ACP properties are camelCase, with one special case: <c>_meta</c>, the extension slot
    /// that appears on nearly every type. It becomes <c>Meta</c> rather than <c>_Meta</c>,
    /// because a leading underscore reads as a private field in C#.
    /// </remarks>
    public static string Property(string jsonName)
    {
        if (jsonName == "_meta")
        {
            return "Meta";
        }

        return Pascal(jsonName);
    }

    /// <summary>
    /// A wire value as an enum member name: <c>allow_once</c> becomes <c>AllowOnce</c>.
    /// </summary>
    /// <remarks>
    /// Vendor extension values begin with <c>_</c> per the ACP extensibility rules. The
    /// underscore is dropped for the member name — the wire value is preserved verbatim
    /// alongside it, so nothing is lost.
    /// </remarks>
    public static string EnumMember(string wireValue)
    {
        var name = Pascal(wireValue.TrimStart('_'));
        return name.Length == 0 ? "Unnamed" : name;
    }

    /// <summary>A definition name as a C# type name. Already PascalCase in ACP.</summary>
    public static string Type(string definitionName) => Pascal(definitionName);

    /// <summary>A method table key as a constant name: <c>session_new</c> becomes <c>SessionNew</c>.</summary>
    public static string Constant(string key) => Pascal(key);

    private static string Pascal(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var upperNext = true;

        foreach (var c in value)
        {
            if (c is '_' or '-' or '/' or '.' or ' ')
            {
                upperNext = true;
                continue;
            }

            if (!char.IsLetterOrDigit(c))
            {
                continue;
            }

            builder.Append(upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }

        var result = builder.ToString();
        if (result.Length > 0 && char.IsDigit(result[0]))
        {
            result = "_" + result;
        }

        return result;
    }
}
