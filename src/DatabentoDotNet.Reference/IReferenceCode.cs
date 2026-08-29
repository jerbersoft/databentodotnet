namespace DatabentoDotNet.Reference;

/// <summary>
/// The contract the ten reference code types share: a wrapper over the wire string that keeps a
/// code this library does not know instead of losing it.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
/// <remarks>
/// <para>
/// <b>Ten of the reference enums are open sets, and a C# <c>enum</c> cannot represent one.</b>
/// Upstream ends each of them in an <c>Unknown(String)</c> variant whose <c>FromStr</c> falls
/// through to it (<c>enums.rs:1139</c> and its siblings), so an ISO code Databento adds next month
/// round-trips through that library untouched. The two obvious .NET answers both give that up:
/// </para>
/// <para>
/// An <c>enum</c> with an <c>Unknown = -1</c> member compiles and <b>loses the string</b> — a caller
/// handed <c>Country.Unknown</c> cannot tell Kosovo from a typo and cannot echo the value back into
/// a filter, which is strictly worse than upstream on the axis upstream chose. A bare
/// <see cref="string"/> is free and gives up every misuse check: nothing would stop a
/// <see cref="Currency"/> reaching a <c>countries</c> filter.
/// </para>
/// <para>
/// So each type is a <c>readonly record struct</c> over the code, with the known values as static
/// members — <c>Country.Us</c> still reads like an enum at a call site. <c>record struct</c> rather
/// than a hand-rolled wrapper is deliberate: it synthesizes <see cref="object.Equals(object)"/> and
/// <see cref="object.GetHashCode"/> that agree with each other over the wrapped string, which a
/// hand-rolled struct gets wrong by default.
/// </para>
/// <para>
/// <b>Which types are open is decided by where the vocabulary comes from, not by upstream's
/// syntax.</b> A single-byte alphabet is closed, because a new value in it is a wire-format change;
/// a code that comes out of Databento's growing data dictionary is not. Probing
/// <c>corporate_actions.list_enums</c> found upstream already behind the live server on two of the
/// sets it models as closed — <see cref="SecurityType"/> at 30 of 64 and <see cref="Frequency"/> at
/// 14 of 16 — and <see cref="OutturnStyle"/> is here beside them despite being exact against the
/// server today, because the rule is about where a vocabulary comes from rather than how many
/// values it currently holds. That is a <b>behavioural</b> departure from upstream and goes one way
/// only: this library accepts rows upstream rejects, never the reverse. ROADMAP.md §6 records it,
/// along with the third set the probe found upstream stale on — <see cref="Event"/>, which is
/// absent from this sentence because upstream already models it as open.
/// </para>
/// <para>
/// <b><see langword="default"/> means "no value", and that is what a blank code deserializes to.</b>
/// The dictionary itself carries blank entries — <c>SECTYPE</c>, <c>FREQ</c> and
/// <c>EVENTSUBTYPE</c> each have one, and 148 of the 235 groups do — so a blank is a real thing the
/// server sends rather than a malformed value. <see cref="From"/> maps it to
/// <see langword="default"/>, whose <see cref="Code"/> is <see langword="null"/> and whose
/// <see cref="HasValue"/> is <see langword="false"/>. The constructor refuses one, so the only way
/// to reach that state is deliberately.
/// </para>
/// </remarks>
public interface IReferenceCode<TSelf>
    where TSelf : struct, IReferenceCode<TSelf>
{
    /// <summary>
    /// Every code the reference API reported for this type when the vendored fixture was captured.
    /// </summary>
    /// <remarks>
    /// A snapshot, not a closed set. <see cref="IsKnown"/> answers against it; nothing rejects a
    /// code outside it.
    /// </remarks>
    static abstract IReadOnlySet<string> KnownCodes { get; }

    /// <summary>
    /// Reads a wire code, mapping <see langword="null"/> and the empty string to
    /// <see langword="default"/>.
    /// </summary>
    /// <param name="code">The wire code, or <see langword="null"/>.</param>
    /// <returns>The value.</returns>
    static abstract TSelf From(string? code);

    /// <summary>
    /// The wire code, or <see langword="null"/> when this names no value.
    /// </summary>
    string? Code { get; }

    /// <summary>
    /// <see langword="true"/> when this names a code — known or not.
    /// </summary>
    bool HasValue { get; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Code"/> is one of <see cref="KnownCodes"/>.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> is not an error. It means the server sent something newer than the
    /// table this library shipped with, and the code is still in <see cref="Code"/> to be read,
    /// logged, or sent back in a filter.
    /// </remarks>
    bool IsKnown { get; }
}
