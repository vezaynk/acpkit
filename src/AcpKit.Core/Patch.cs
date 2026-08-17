using System.Diagnostics.CodeAnalysis;

namespace AcpKit;

/// <summary>
/// A field that is either absent, explicitly cleared, or set to a value.
/// </summary>
/// <remarks>
/// <para>
/// ACP v2 builds its update model on the difference between those three. On a
/// <c>tool_call_update</c>, an omitted <c>title</c> means "leave the existing title alone",
/// a <c>null</c> title means "clear it", and a string means "replace it". A plain nullable
/// property cannot tell the first two apart, so a client built on one either loses every
/// clear or invents one on every partial update.
/// </para>
/// <para>
/// <see cref="Unset"/> is deliberately <c>default</c>. That lets the generated property carry
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]</c>, so an untouched
/// field is simply not written and the omission falls out of the serializer rather than
/// needing bookkeeping at every call site.
/// </para>
/// </remarks>
/// <typeparam name="T">The value type when the field is set.</typeparam>
public readonly struct Patch<T> : IEquatable<Patch<T>>
{
    private enum State : byte
    {
        Unset = 0,
        Cleared = 1,
        Set = 2,
    }

    private readonly State _state;
    private readonly T? _value;

    private Patch(State state, T? value)
    {
        _state = state;
        _value = value;
    }

    /// <summary>The field is absent: leave whatever is already there unchanged.</summary>
    public static Patch<T> Unset => default;

    /// <summary>The field is explicitly <c>null</c>: clear whatever is already there.</summary>
    public static Patch<T> Cleared => new(State.Cleared, default);

    /// <summary>The field carries a value: replace whatever is already there.</summary>
    public static Patch<T> Set(T value) => new(State.Set, value);

    /// <summary>True when the field was omitted.</summary>
    public bool IsUnset => _state == State.Unset;

    /// <summary>True when the field was sent as <c>null</c>.</summary>
    public bool IsCleared => _state == State.Cleared;

    /// <summary>True when the field carries a value.</summary>
    public bool HasValue => _state == State.Set;

    /// <summary>
    /// The value, when there is one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The field was omitted or cleared.</exception>
    public T Value => _state == State.Set
        ? _value!
        : throw new InvalidOperationException(
            $"This patch is {(_state == State.Unset ? "unset" : "cleared")} and carries no value.");

    /// <summary>The value when set, otherwise <paramref name="fallback"/>.</summary>
    public T? GetValueOrDefault(T? fallback = default) => _state == State.Set ? _value : fallback;

    /// <summary>Read the value without throwing.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return _state == State.Set;
    }

    /// <summary>
    /// Apply this patch to a current value, answering what the field becomes.
    /// </summary>
    /// <remarks>
    /// The whole point of the type in one call: this is what a client applies when folding a
    /// <c>tool_call_update</c> into the tool call it already holds.
    /// </remarks>
    public T? ApplyTo(T? current) => _state switch
    {
        State.Set => _value,
        State.Cleared => default,
        _ => current,
    };

    /// <summary>Wrap a value. <see langword="null"/> becomes <see cref="Cleared"/>, not <see cref="Unset"/>.</summary>
    public static implicit operator Patch<T>(T? value) => value is null ? Cleared : Set(value);

    /// <inheritdoc/>
    public bool Equals(Patch<T> other) =>
        _state == other._state && EqualityComparer<T?>.Default.Equals(_value, other._value);

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Patch<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine((byte)_state, _value);

    /// <inheritdoc/>
    public override string ToString() => _state switch
    {
        State.Set => _value?.ToString() ?? string.Empty,
        State.Cleared => "(cleared)",
        _ => "(unset)",
    };

    /// <summary>Whether two patches are in the same state and carry the same value.</summary>
    public static bool operator ==(Patch<T> left, Patch<T> right) => left.Equals(right);

    /// <summary>Whether two patches differ.</summary>
    public static bool operator !=(Patch<T> left, Patch<T> right) => !left.Equals(right);
}
