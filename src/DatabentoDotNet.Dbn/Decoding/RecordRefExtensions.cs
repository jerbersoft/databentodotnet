using NodaTime;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The date half of a record's index timestamp: the calendar day a symbol map is keyed by.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>Record::index_date</c> (<c>record/traits.rs:68-70</c>), which is
/// <c>index_ts().map(|dt| dt.date())</c> — the record's own primary timestamp, reduced to its UTC
/// date. <see cref="RecordRef.IndexTs"/> already carries the per-record-type choice between
/// <c>ts_recv</c> and <c>ts_event</c>, so everything here is the conversion and nothing else.
/// </para>
/// <para>
/// <b>Extension methods on <see cref="RecordRef"/> rather than members on
/// <see cref="IRecord{TSelf}"/>, and only on <see cref="RecordRef"/>.</b> Upstream's
/// <c>index_date()</c> is a default method on the <c>Record</c> trait, so it reaches every record
/// type; the direct .NET translation does not exist. An interface member would be twenty-one
/// copies of one line, and a default interface member cannot replace them — calling one on a
/// record struct needs either boxing or a generic constraint, and boxing a 520-byte
/// <see cref="InstrumentDefMsg"/> to read a date is the copy this codec exists to avoid. A
/// generic extension method cannot take its receiver by <see langword="in"/> at all (CS8338, and
/// C# 14's extension blocks keep the restriction as CS9301), so it would copy the record by
/// value for the same 520 bytes.
/// </para>
/// <para>
/// <b>For a concrete record struct, the conversion is already a documented one-liner:</b>
/// <c>DbnTime.TryToUtcDate(def.IndexTs, out var date)</c>. It is the same length as a helper
/// would be, it reads the field in place off a <see langword="ref"/> <see langword="readonly"/>,
/// and it goes through the one sentinel-checking crossing rather than adding a second name for
/// it. What <see cref="RecordRef"/> gets here that a concrete struct does not need is the rtype
/// dispatch — hiding "which timestamp does this record type index on" behind one call is the
/// whole value, and a concrete struct has already answered that question by being concrete.
/// </para>
/// <para>
/// <see cref="WithTsOut{T}"/> is covered by the same one-liner through
/// <see cref="WithTsOut{T}.IndexTs"/>, which forwards to the wrapped record unchanged —
/// <c>ts_out</c> is the gateway's send time and never an index timestamp.
/// </para>
/// </remarks>
public static class RecordRefExtensions
{
    /// <summary>
    /// The UTC calendar date this record indexes on, unless its index timestamp is DBN's
    /// undefined sentinel.
    /// </summary>
    /// <remarks>
    /// This is the date <see cref="TsSymbolMap.TryGetSymbol(LocalDate, uint, out string?)"/> is
    /// keyed by. Prefer it to converting <see cref="RecordHeader.TsEvent"/> yourself: most
    /// schemas index on <c>ts_recv</c>, and the two can fall on opposite sides of UTC midnight,
    /// so keying on <c>ts_event</c> returns the previous day's symbol — or nothing — with nothing
    /// anywhere looking broken.
    /// </remarks>
    /// <param name="record">The record to read.</param>
    /// <param name="date">
    /// Receives the UTC date, or <see langword="default"/> when the record's index timestamp is
    /// <see cref="DbnConstants.UndefTimestamp"/>.
    /// </param>
    /// <returns><see langword="false"/> when the index timestamp is undefined.</returns>
    public static bool TryIndexDate(this RecordRef record, out LocalDate date)
        => DbnTime.TryToUtcDate(record.IndexTs, out date);

    /// <summary>The UTC calendar date this record indexes on.</summary>
    /// <remarks>
    /// <see cref="DbnTime.ToUtcDate"/> answers the same failure with an
    /// <see cref="ArgumentOutOfRangeException"/> naming its <c>unixNanoseconds</c> parameter.
    /// That is right there and wrong here: this overload takes no timestamp argument, so an
    /// <see cref="ArgumentOutOfRangeException"/> would name a parameter the caller never passed.
    /// The condition is a property of the record, which is what
    /// <see cref="InvalidOperationException"/> says — the same reading
    /// <see cref="RecordRef.TsOut"/> already gives it.
    /// </remarks>
    /// <param name="record">The record to read.</param>
    /// <returns>The UTC date.</returns>
    /// <exception cref="InvalidOperationException">
    /// The record's index timestamp is <see cref="DbnConstants.UndefTimestamp"/>, so it has no
    /// date. Use <see cref="TryIndexDate"/> where a record without one is expected.
    /// </exception>
    public static LocalDate IndexDate(this RecordRef record)
        => record.TryIndexDate(out var date)
            ? date
            : throw new InvalidOperationException(
                "This record's index timestamp is the undefined sentinel, so it has no date.");
}
