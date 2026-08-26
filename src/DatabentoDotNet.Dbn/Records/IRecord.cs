namespace DatabentoDotNet.Dbn;

/// <summary>
/// Associates a record struct with the record types (<see cref="RType"/>) it can decode and
/// with its exact size on the wire.
/// </summary>
/// <typeparam name="TSelf">The implementing record struct.</typeparam>
/// <remarks>
/// <para>
/// This is the port of the Rust crate's <c>HasRType</c> trait, and it has the same shape as
/// <c>databento-cpp</c>'s per-struct <c>static bool HasRType(RType)</c>. C# 11 static abstract
/// interface members resolve the check at the call site with no allocation, no boxing, and no
/// reflection, which keeps the downcast path AOT- and trim-safe.
/// </para>
/// <para>
/// <strong>An rtype alone does not identify a record.</strong> Five rtypes —
/// <see cref="RType.InstrumentDef"/>, <see cref="RType.SymbolMapping"/>,
/// <see cref="RType.Error"/>, <see cref="RType.System"/> and <see cref="RType.Statistics"/> —
/// decode to a different struct depending on the record's length, because those layouts changed
/// across DBN versions. The match rule is therefore
/// <c>T.HasRType(rtype) &amp;&amp; wireLength == T.WireSize</c>, with <em>exact</em> equality: a
/// <c>&gt;=</c> comparison would let a 520-byte v3 <c>InstrumentDefMsg</c> match the 360-byte v1
/// struct and silently decode as the wrong version. No two versions of the same rtype share a
/// size, so exact equality disambiguates every family.
/// </para>
/// </remarks>
public interface IRecord<TSelf>
    where TSelf : unmanaged, IRecord<TSelf>
{
    /// <summary>
    /// Reports whether <typeparamref name="TSelf"/> is the layout for records carrying
    /// <paramref name="rtype"/>.
    /// </summary>
    /// <param name="rtype">The record type read from a record header.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="rtype"/> is one of the record types this struct
    /// decodes. This is necessary but not sufficient to identify the record — see the remarks on
    /// <see cref="IRecord{TSelf}"/>.
    /// </returns>
    static abstract bool HasRType(RType rtype);

    /// <summary>
    /// The record's index timestamp: the one to sort by, and the one to key a symbol map with.
    /// Nanoseconds since the UNIX epoch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is usually <c>ts_recv</c>, not <see cref="RecordHeader.TsEvent"/>.</b> Fourteen of
    /// the twenty-one record structs carry a <c>ts_recv</c> and index on it; the rest have no
    /// <c>ts_recv</c> at all and fall back to the header's <c>ts_event</c>. Port of upstream's
    /// <c>Record::raw_index_ts</c> (<c>record/traits.rs:52-54</c>), whose default body is
    /// <c>ts_event</c> and which the <c>#[dbn(index_ts)]</c> field attribute overrides per struct
    /// (<c>record.rs</c>, <c>compat.rs</c>).
    /// </para>
    /// <para>
    /// The distinction is not cosmetic: <c>ts_event</c> and <c>ts_recv</c> can fall on opposite
    /// sides of UTC midnight, so resolving a symbol by the wrong one silently returns the
    /// previous day's symbol, or nothing, with no error anywhere.
    /// </para>
    /// <para>
    /// This is a raw timestamp and can be <see cref="DbnConstants.UndefTimestamp"/>. Convert it
    /// with <see cref="DbnTime.ToUtcDate"/> or <see cref="DbnTime.TryToUtcDate"/>, which check
    /// the sentinel.
    /// </para>
    /// </remarks>
    ulong IndexTs { get; }

    /// <summary>
    /// The struct's exact size on the wire, in bytes, excluding any trailing <c>ts_out</c>.
    /// </summary>
    static abstract int WireSize { get; }
}
