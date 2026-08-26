namespace DatabentoDotNet.Dbn;

/// <summary>
/// Conversions between <see cref="RType"/> and <see cref="Schema"/>.
/// </summary>
/// <remarks>
/// The two directions are not exact inverses. <see cref="Schema.Mbp1"/> and
/// <see cref="Schema.Tbbo"/> both map to <see cref="RType.Mbp1"/> via
/// <see cref="ToRType(Schema)"/>, so <see cref="TryIntoSchema(RType, out Schema)"/> can never
/// recover <see cref="Schema.Tbbo"/> from an <see cref="RType.Mbp1"/> byte — it always yields
/// <see cref="Schema.Mbp1"/>. This mirrors the Rust crate's <c>impl From&lt;Schema&gt; for
/// RType</c> and <c>RType::try_into_schema</c> exactly; the further collapse between
/// <see cref="RType.Cmbp1"/>/<see cref="RType.Tcbbo"/> at the record-struct dispatch level is
/// out of scope here (it lands with record decoding).
/// </remarks>
public static class RTypeSchemaMapping
{
    /// <summary>Returns the canonical <see cref="RType"/> for <paramref name="schema"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="schema"/> is not a defined <see cref="Schema"/>.</exception>
    public static RType ToRType(this Schema schema) => schema switch
    {
        Schema.Mbo => RType.Mbo,
        Schema.Mbp1 or Schema.Tbbo => RType.Mbp1,
        Schema.Mbp10 => RType.Mbp10,
        Schema.Trades => RType.Mbp0,
        Schema.Ohlcv1S => RType.Ohlcv1S,
        Schema.Ohlcv1M => RType.Ohlcv1M,
        Schema.Ohlcv1H => RType.Ohlcv1H,
        Schema.Ohlcv1D => RType.Ohlcv1D,
        Schema.OhlcvEod => RType.OhlcvEod,
        Schema.Definition => RType.InstrumentDef,
        Schema.Statistics => RType.Statistics,
        Schema.Status => RType.Status,
        Schema.Imbalance => RType.Imbalance,
        Schema.Cmbp1 => RType.Cmbp1,
        Schema.Cbbo1S => RType.Cbbo1S,
        Schema.Cbbo1M => RType.Cbbo1M,
        Schema.Tcbbo => RType.Tcbbo,
        Schema.Bbo1S => RType.Bbo1S,
        Schema.Bbo1M => RType.Bbo1M,
        _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, "Undefined Schema."),
    };

    /// <summary>
    /// Tries to convert an <see cref="RType"/> — as carried typed by
    /// <see cref="RecordHeader.RType"/> — into the <see cref="Schema"/> it represents.
    /// </summary>
    /// <remarks>
    /// The typed counterpart of <see cref="ToRType(Schema)"/>, and the overload to reach for from
    /// a decoded record. It delegates to the <see cref="byte"/> overload, which stays public for
    /// callers holding an unvalidated wire byte — an rtype this library has no member for is a
    /// <see langword="false"/> here either way, so the two never disagree.
    /// </remarks>
    /// <param name="rtype">The record type read from a record header.</param>
    /// <param name="schema">Receives the schema, or <see langword="default"/> when there is none.</param>
    /// <returns><see langword="true"/> if <paramref name="rtype"/> maps to a schema.</returns>
    public static bool TryIntoSchema(RType rtype, out Schema schema)
        => TryIntoSchema((byte)rtype, out schema);

    /// <summary>
    /// Tries to convert a raw <c>rtype</c> byte (as carried by
    /// <see cref="RecordHeader.RawRType"/>) into the <see cref="Schema"/> it represents.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for <see cref="RType.OhlcvDeprecated"/> — it predates the
    /// per-cadence OHLCV rtypes and does not map to a single schema — and for the control/meta
    /// rtypes <see cref="RType.Error"/>, <see cref="RType.SymbolMapping"/>, and
    /// <see cref="RType.System"/>, none of which carry an associated schema. Also returns
    /// <see langword="false"/> for any byte that is not a defined <see cref="RType"/> at all.
    /// </remarks>
    public static bool TryIntoSchema(byte rtype, out Schema schema)
    {
        switch (rtype)
        {
            case (byte)RType.Mbp0: schema = Schema.Trades; return true;
            case (byte)RType.Mbp1: schema = Schema.Mbp1; return true;
            case (byte)RType.Mbp10: schema = Schema.Mbp10; return true;
            case (byte)RType.Ohlcv1S: schema = Schema.Ohlcv1S; return true;
            case (byte)RType.Ohlcv1M: schema = Schema.Ohlcv1M; return true;
            case (byte)RType.Ohlcv1H: schema = Schema.Ohlcv1H; return true;
            case (byte)RType.Ohlcv1D: schema = Schema.Ohlcv1D; return true;
            case (byte)RType.OhlcvEod: schema = Schema.OhlcvEod; return true;
            case (byte)RType.Status: schema = Schema.Status; return true;
            case (byte)RType.InstrumentDef: schema = Schema.Definition; return true;
            case (byte)RType.Imbalance: schema = Schema.Imbalance; return true;
            case (byte)RType.Statistics: schema = Schema.Statistics; return true;
            case (byte)RType.Mbo: schema = Schema.Mbo; return true;
            case (byte)RType.Cmbp1: schema = Schema.Cmbp1; return true;
            case (byte)RType.Cbbo1S: schema = Schema.Cbbo1S; return true;
            case (byte)RType.Cbbo1M: schema = Schema.Cbbo1M; return true;
            case (byte)RType.Tcbbo: schema = Schema.Tcbbo; return true;
            case (byte)RType.Bbo1S: schema = Schema.Bbo1S; return true;
            case (byte)RType.Bbo1M: schema = Schema.Bbo1M; return true;
            default: schema = default; return false;
        }
    }
}
