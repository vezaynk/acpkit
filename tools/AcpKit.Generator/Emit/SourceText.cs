using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;

namespace AcpKit.Generator.Emit;

/// <summary>
/// Turns a syntax tree into the text that gets written to disk.
/// </summary>
/// <remarks>
/// Generated sources here are checked in and reviewed by people. A schema bump arrives as a
/// diff someone has to scan for protocol drift, so odd spacing is not cosmetic — it is noise
/// competing with the signal.
/// </remarks>
internal static partial class SourceText
{
    private static readonly AdhocWorkspace Workspace = new();

    /// <summary>Format a compilation unit and render it, with a trailing newline.</summary>
    public static string Render(SyntaxNode unit)
    {
        // NormalizeWhitespace makes the tree printable; Formatter makes it idiomatic, applying
        // the rules an IDE would. Formatting options live on a workspace, which is the only
        // reason one exists here; it is reused because creating one per file is slower.
        var normalized = unit.NormalizeWhitespace(eol: "\n");
        var formatted = Formatter.Format(normalized, Workspace, Workspace.Options);
        return Reindent(RepairDocComments(formatted.ToFullString().TrimStart())) + "\n";
    }

    /// <summary>
    /// Repair the two things neither pass gets right, both of them inside documentation
    /// comments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>NormalizeWhitespace</c> treats a doc comment's XML as syntax and puts spaces around
    /// attribute equals signs, so <c>see cref="T"</c> becomes <c>see cref = "T"</c>. It also
    /// re-indents only the <em>first</em> line of a multi-line comment, leaving the rest at
    /// whatever depth they were parsed at — which is how every summary here ended up with its
    /// opening tag four spaces to the left of its own body. <see cref="Formatter"/> fixes
    /// neither, because it does not descend into trivia.
    /// </para>
    /// <para>
    /// A textual repair is worth being uneasy about, so it is narrow on purpose. It runs after
    /// the tree is final, and it only rewrites lines whose content is entirely a <c>///</c>
    /// comment — where no C# expression can appear. Applied to the file as a whole, the
    /// attribute fix would have corrupted every <c>const string X = "..."</c> in the generated
    /// method tables.
    /// </para>
    /// </remarks>
    private static string RepairDocComments(string source)
    {
        var lines = source.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsLeadingTrivia(lines[i]))
            {
                continue;
            }

            // Trivia belongs to whatever it decorates, so the declaration below the block is the
            // authority on how far in the whole thing sits.
            var end = i;
            while (end < lines.Length && IsLeadingTrivia(lines[end]))
            {
                end++;
            }

            var indent = end < lines.Length ? LeadingWhitespace(lines[end]) : LeadingWhitespace(lines[i]);

            for (var j = i; j < end; j++)
            {
                var content = lines[j].TrimStart();
                lines[j] = indent + (IsDocComment(lines[j]) ? XmlAttribute().Replace(content, "$1=\"") : content);
            }

            i = end;
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Whether a line is leading trivia: a documentation comment, or an attribute written
    /// entirely on one line.
    /// </summary>
    /// <remarks>
    /// The attribute test deliberately requires a letter after the bracket and a closing bracket
    /// on the same line. A bare <c>[</c> opening a multi-line collection expression — which the
    /// generated type catalogue is full of — must not be mistaken for one.
    /// </remarks>
    private static bool IsLeadingTrivia(string line)
    {
        if (IsDocComment(line))
        {
            return true;
        }

        var trimmed = line.AsSpan().Trim();
        return trimmed.Length > 2
            && trimmed[0] == '['
            && char.IsLetter(trimmed[1])
            && trimmed[^1] == ']';
    }

    /// <summary>
    /// Re-indent the whole file from brace and bracket depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Roslyn's own indentation is not consistent across declaration kinds here: class members
    /// land at one level, interface members at two, and no combination of template indentation
    /// changes it. Rather than keep guessing at the formatter, indentation is computed from the
    /// structure that is actually visible in the text.
    /// </para>
    /// <para>
    /// Safe because of what generated code does not contain: no multi-line string literals, no
    /// raw strings, and no verbatim strings spanning lines, so no brace or bracket in the file
    /// is anything other than structure.
    /// </para>
    /// </remarks>
    private static string Reindent(string source)
    {
        var lines = source.Split('\n');
        var depth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var content = lines[i].Trim();
            if (content.Length == 0)
            {
                lines[i] = string.Empty;
                continue;
            }

            // A line that closes a block belongs to the level it is closing, not the one inside.
            var closesFirst = content[0] is '}' or ']' or ')';
            var indent = Math.Max(0, closesFirst ? depth - 1 : depth);
            lines[i] = new string(' ', indent * 4) + content;

            depth += Delta(content);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// How many levels a line opens, net of what it closes.
    /// </summary>
    /// <remarks>
    /// Comments and string literals are skipped, because their brackets are prose rather than
    /// structure. A schema description reading "as if `session/cancel` was called)" would
    /// otherwise close a level that nothing opened, and every declaration after it in the file
    /// would sit one indent too far left.
    /// </remarks>
    private static int Delta(string content)
    {
        if (content.StartsWith("//", StringComparison.Ordinal))
        {
            return 0;
        }

        var delta = 0;
        var inString = false;
        var inChar = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (c == '\\' && (inString || inChar))
            {
                i++;
                continue;
            }

            if (c == '"' && !inChar)
            {
                inString = !inString;
                continue;
            }

            if (c == '\'' && !inString)
            {
                inChar = !inChar;
                continue;
            }

            if (inString || inChar)
            {
                continue;
            }

            // A line comment ends the code on this line; anything after it is prose.
            if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                break;
            }

            if (c is '{' or '[' or '(')
            {
                delta++;
            }
            else if (c is '}' or ']' or ')')
            {
                delta--;
            }
        }

        return delta;
    }

    private static bool IsDocComment(string line) => line.AsSpan().TrimStart().StartsWith("///");

    private static string LeadingWhitespace(string line)
    {
        var length = 0;
        while (length < line.Length && (line[length] == ' ' || line[length] == '\t'))
        {
            length++;
        }

        return line[..length];
    }

    /// <summary>An XML attribute whose equals sign has acquired surrounding spaces.</summary>
    [GeneratedRegex("(\\w+) = \"")]
    private static partial Regex XmlAttribute();
}
