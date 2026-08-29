using System.Collections.Frozen;
using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// Which of a corporate action's three open field maps a field belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>An open set: a code this library does not know is carried, not lost.</b> Upstream ends
/// this enum in an <c>Unknown(String)</c> variant (<c>enums.rs:2681</c>) so a code Databento adds
/// next month round-trips untouched, and a C# <c>enum</c> cannot hold a payload. See
/// <see cref="IReferenceCode{TSelf}"/> for the shape this takes instead and why.
/// </para>
/// <para>
/// <c>corporate_actions.list_enums</c> has no group for this type, so <c>corporate_actions.list_events</c> is its only authority — and the two agree exactly, upstream included.
/// </para>
/// <para>
/// Names <c>event_info</c>, <c>date_info</c> and <c>rate_info</c> — the three maps <c>CorporateAction</c> carries (<c>corporate.rs:433-438</c>). Load-bearing for the <c>list_events</c> documentation as well as for a response field.
/// </para>
/// </remarks>
[JsonConverter(typeof(ReferenceCodeJsonConverter<FieldGroup>))]
public readonly record struct FieldGroup : IReferenceCode<FieldGroup>
{
    private static readonly FrozenSet<string> Codes = FrozenSet.ToFrozenSet(
    [
        "date_info",
        "event_info",
        "rate_info",
    ], StringComparer.Ordinal);

    private readonly string? _code;

    /// <summary>
    /// Wraps a wire code, known or not. Prefer a named member such as
    /// <see cref="DateInfo"/> where one exists, and <see cref="From"/> where the value came
    /// from the server.
    /// </summary>
    /// <param name="code">The wire code.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty. A blank code is the absence of a value, which is <see langword="default"/>.</exception>
    public FieldGroup(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        _code = code;
    }

    /// <summary>
    /// Every code the reference API reported for this type when the fixture was captured —
    /// 3 of them.
    /// </summary>
    public static IReadOnlySet<string> KnownCodes => Codes;

    /// <inheritdoc/>
    public string? Code => _code;

    /// <inheritdoc/>
    public bool HasValue => _code is not null;

    /// <inheritdoc/>
    public bool IsKnown => _code is not null && Codes.Contains(_code);

    /// <summary>
    /// Reads a wire code, mapping <see langword="null"/> and the empty string to
    /// <see langword="default"/> — the absence of a value.
    /// </summary>
    /// <param name="code">The wire code, or <see langword="null"/>.</param>
    /// <returns>The value.</returns>
    public static FieldGroup From(string? code) => string.IsNullOrEmpty(code) ? default : new(code);

    /// <summary>The wire code, or the empty string when this names no value.</summary>
    /// <returns>The wire code.</returns>
    public override string ToString() => _code ?? string.Empty;

    /// <summary>date_info (<c>date_info</c>).</summary>
    public static FieldGroup DateInfo => new("date_info");

    /// <summary>event_info (<c>event_info</c>).</summary>
    public static FieldGroup EventInfo => new("event_info");

    /// <summary>rate_info (<c>rate_info</c>).</summary>
    public static FieldGroup RateInfo => new("rate_info");
}
