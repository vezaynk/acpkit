using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace AcpKit;

/// <summary>
/// A JSON-RPC request identifier: a number or a string, per JSON-RPC 2.0 §4.
/// </summary>
/// <remarks>
/// <para>
/// AcpKit always sends numeric ids. It accepts either on the way back, and — this is the
/// part that matters in practice — it treats a <em>numeric string</em> as equal to the
/// number it spells. Real agents echo the id they were sent as a string
/// (<c>"id": "7"</c> in response to <c>"id": 7</c>), and a correlation table that does not
/// allow for that simply hangs: the response arrives, matches nothing, and the request
/// waits forever on a reply it has already been given.
/// </para>
/// <para>
/// The normalisation is one-way on purpose. <c>"7"</c> normalises to <c>7</c>, but
/// <c>"abc"</c> stays a string, so an agent that mints genuinely non-numeric ids still
/// round-trips exactly as it sent them.
/// </para>
/// </remarks>
public readonly struct RequestId : IEquatable<RequestId>
{
    private readonly long _number;
    private readonly string? _text;

    private RequestId(long number)
    {
        _number = number;
        _text = null;
    }

    private RequestId(string text)
    {
        _number = 0;
        _text = text;
    }

    /// <summary>A numeric id.</summary>
    public static RequestId FromNumber(long value) => new(value);

    /// <summary>
    /// A string id, normalised to a numeric id when the text spells one exactly.
    /// </summary>
    public static RequestId FromString(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? new RequestId(number)
            : new RequestId(value);

    /// <summary>True when this id is carried on the wire as a JSON number.</summary>
    public bool IsNumber => _text is null;

    /// <summary>
    /// Read an id from the <c>id</c> member of a JSON-RPC message. Answers false for
    /// <c>null</c>, a missing member, or any other JSON kind — all of which mean the
    /// message is a notification or is malformed.
    /// </summary>
    public static bool TryRead(JsonElement element, out RequestId id)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out var number):
                id = new RequestId(number);
                return true;
            case JsonValueKind.String:
                id = FromString(element.GetString()!);
                return true;
            default:
                id = default;
                return false;
        }
    }

    /// <summary>Write this id as the value of an <c>id</c> member.</summary>
    public void Write(Utf8JsonWriter writer, string propertyName)
    {
        if (_text is null)
        {
            writer.WriteNumber(propertyName, _number);
        }
        else
        {
            writer.WriteString(propertyName, _text);
        }
    }

    /// <inheritdoc/>
    public bool Equals(RequestId other) =>
        _text is null && other._text is null
            ? _number == other._number
            : string.Equals(_text, other._text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is RequestId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _text?.GetHashCode(StringComparison.Ordinal) ?? _number.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => _text ?? _number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Equality, treating a numeric string as the number it spells.</summary>
    public static bool operator ==(RequestId left, RequestId right) => left.Equals(right);

    /// <summary>Inequality, treating a numeric string as the number it spells.</summary>
    public static bool operator !=(RequestId left, RequestId right) => !left.Equals(right);
}
