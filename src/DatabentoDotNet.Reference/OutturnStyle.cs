using System.Collections.Frozen;
using System.Text.Json.Serialization;
using DatabentoDotNet.Reference.Json;

namespace DatabentoDotNet.Reference;

/// <summary>
/// Whether an outturn security is new or additional to an existing holding.
/// </summary>
/// <remarks>
/// <para>
/// <b>An open set: a code this library does not know is carried, not lost.</b> Upstream ends
/// this enum in an <c>Unknown(String)</c> variant (<c>enums.rs:3157</c>) so a code Databento adds
/// next month round-trips untouched, and a C# <c>enum</c> cannot hold a payload. See
/// <see cref="IReferenceCode{TSelf}"/> for the shape this takes instead and why.
/// </para>
/// <para>
/// The members come from the <c>OUTTURNSTYLE</c> group of the vendored <c>corporate_actions.list_enums</c> response, which is the oracle rather than a count typed into an issue.
/// </para>
/// <para>
/// Exact against the live dictionary today, at two codes each, and an open carrier anyway: the rule is where a vocabulary comes from, not how many values it currently holds.
/// </para>
/// </remarks>
[JsonConverter(typeof(ReferenceCodeJsonConverter<OutturnStyle>))]
public readonly record struct OutturnStyle : IReferenceCode<OutturnStyle>
{
    private static readonly FrozenSet<string> Codes = FrozenSet.ToFrozenSet(
    [
        "ADEX",
        "NEWO",
    ], StringComparer.Ordinal);

    private readonly string? _code;

    /// <summary>
    /// Wraps a wire code, known or not. Prefer a named member such as
    /// <see cref="Adex"/> where one exists, and <see cref="From"/> where the value came
    /// from the server.
    /// </summary>
    /// <param name="code">The wire code.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is null or empty. A blank code is the absence of a value, which is <see langword="default"/>.</exception>
    public OutturnStyle(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        _code = code;
    }

    /// <summary>
    /// Every code the reference API reported for this type when the fixture was captured —
    /// 2 of them.
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
    public static OutturnStyle From(string? code) => string.IsNullOrEmpty(code) ? default : new(code);

    /// <summary>The wire code, or the empty string when this names no value.</summary>
    /// <returns>The wire code.</returns>
    public override string ToString() => _code ?? string.Empty;

    /// <summary>Additional for Existing Securities (<c>ADEX</c>).</summary>
    public static OutturnStyle Adex => new("ADEX");

    /// <summary>New for Old Securities (<c>NEWO</c>).</summary>
    public static OutturnStyle Newo => new("NEWO");
}
