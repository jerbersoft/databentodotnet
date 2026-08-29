using System.Collections.Frozen;
using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// The category a corporate-action event falls into.
/// </summary>
/// <remarks>
/// <para>
/// <b>An open set: a code this library does not know is carried, not lost.</b> Upstream ends
/// this enum in an <c>Unknown(String)</c> variant (<c>enums.rs:2221</c>) so a code Databento adds
/// next month round-trips untouched, and a C# <c>enum</c> cannot hold a payload. See
/// <see cref="IReferenceCode{TSelf}"/> for the shape this takes instead and why.
/// </para>
/// <para>
/// <c>corporate_actions.list_enums</c> has no group for this type, so <c>corporate_actions.list_events</c> is its only authority — and the two agree exactly, upstream included.
/// </para>
/// <para>
/// <see cref="Other"/> is a value the server sends and is not the same thing as an unrecognised code. A code the server adds later is carried intact and reports <see cref="IsKnown"/> <see langword="false"/>; <c>other</c> is known and means <em>other</em>.
/// </para>
/// </remarks>
[JsonConverter(typeof(ReferenceCodeJsonConverter<EventCategory>))]
public readonly record struct EventCategory : IReferenceCode<EventCategory>
{
    private static readonly FrozenSet<string> Codes = FrozenSet.ToFrozenSet(
    [
        "distribution",
        "distribution_debit",
        "legal_action",
        "other",
        "proposals",
        "reorganisation",
        "static_reference",
        "tax_related",
    ], StringComparer.Ordinal);

    private readonly string? _code;

    /// <summary>
    /// Wraps a wire code, known or not. Prefer a named member such as
    /// <see cref="Distribution"/> where one exists, and <see cref="From"/> where the value came
    /// from the server.
    /// </summary>
    /// <param name="code">The wire code.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty. A blank code is the absence of a value, which is <see langword="default"/>.</exception>
    public EventCategory(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        _code = code;
    }

    /// <summary>
    /// Every code the reference API reported for this type when the fixture was captured —
    /// 8 of them.
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
    public static EventCategory From(string? code) => string.IsNullOrEmpty(code) ? default : new(code);

    /// <summary>The wire code, or the empty string when this names no value.</summary>
    /// <returns>The wire code.</returns>
    public override string ToString() => _code ?? string.Empty;

    /// <summary>distribution (<c>distribution</c>).</summary>
    public static EventCategory Distribution => new("distribution");

    /// <summary>distribution_debit (<c>distribution_debit</c>).</summary>
    public static EventCategory DistributionDebit => new("distribution_debit");

    /// <summary>legal_action (<c>legal_action</c>).</summary>
    public static EventCategory LegalAction => new("legal_action");

    /// <summary>other (<c>other</c>).</summary>
    public static EventCategory Other => new("other");

    /// <summary>proposals (<c>proposals</c>).</summary>
    public static EventCategory Proposals => new("proposals");

    /// <summary>reorganisation (<c>reorganisation</c>).</summary>
    public static EventCategory Reorganisation => new("reorganisation");

    /// <summary>static_reference (<c>static_reference</c>).</summary>
    public static EventCategory StaticReference => new("static_reference");

    /// <summary>tax_related (<c>tax_related</c>).</summary>
    public static EventCategory TaxRelated => new("tax_related");
}
