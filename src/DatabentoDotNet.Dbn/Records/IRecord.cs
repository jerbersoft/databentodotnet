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
    /// The struct's exact size on the wire, in bytes, excluding any trailing <c>ts_out</c>.
    /// </summary>
    static abstract int WireSize { get; }
}
