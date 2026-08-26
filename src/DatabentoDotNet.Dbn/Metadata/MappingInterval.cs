using NodaTime;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The symbol a raw symbol resolved to over one half-open date range.
/// </summary>
/// <remarks>
/// <para>
/// The range is <b>half-open</b>: <paramref name="StartDate"/> is inclusive and
/// <paramref name="EndDate"/> is exclusive. Symbol-map lookups depend on that convention, so a
/// date equal to <paramref name="EndDate"/> belongs to the <em>next</em> interval, not this one.
/// </para>
/// <para>
/// A <c>readonly record struct</c> rather than a class: it carries one reference and two 4-byte
/// dates, a definition file can hold dozens of intervals per mapping, and value equality is what
/// a caller comparing two intervals actually means. Upstream is a plain <c>struct</c> with
/// <c>PartialEq</c>, which is the same thing.
/// </para>
/// <para>
/// The dates are NodaTime <see cref="LocalDate"/>, per CLAUDE.md — a calendar date with no zone,
/// which is exactly what the wire carries here: a <c>u32</c> holding the decimal digits
/// <c>YYYYMMDD</c>, with no time and no offset anywhere in it. Note this is the one date in DBN
/// metadata that is <em>not</em> a nanosecond timestamp; <see cref="Metadata.Start"/> and
/// <see cref="Metadata.End"/> are the other kind.
/// </para>
/// </remarks>
/// <param name="StartDate">The UTC start of the interval, inclusive.</param>
/// <param name="EndDate">The UTC end of the interval, exclusive.</param>
/// <param name="Symbol">The resolved symbol for this interval, in the stream's output symbology.</param>
public readonly record struct MappingInterval(LocalDate StartDate, LocalDate EndDate, string Symbol);
