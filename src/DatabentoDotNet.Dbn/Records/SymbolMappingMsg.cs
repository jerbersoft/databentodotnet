using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A symbol mapping from the live API, mapping a symbol from one <see cref="SType"/> to another
/// over an interval.
/// </summary>
/// <remarks>
/// This is the DBN v2 layout, unchanged in v3, at 176 bytes. Version 1 is 80 bytes and has no
/// symbology-type fields at all — see <see cref="SymbolMappingMsgV1"/>. The record has no
/// <c>ts_recv</c>, so its index timestamp is <see cref="RecordHeader.TsEvent"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SymbolMappingMsg : IRecord<SymbolMappingMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>
    /// The raw wire byte behind <see cref="StypeIn"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSType(byte, out SType)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawStypeIn;

    /// <summary>The input symbol.</summary>
    public readonly CStr71 StypeInSymbol;

    /// <summary>
    /// The raw wire byte behind <see cref="StypeOut"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSType(byte, out SType)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawStypeOut;

    /// <summary>The output symbol. Always a <see cref="SType.RawSymbol"/>.</summary>
    public readonly CStr71 StypeOutSymbol;

    /// <summary>
    /// The start of the mapping interval, in nanoseconds since the UNIX epoch.
    /// </summary>
    public readonly ulong StartTs;

    /// <summary>
    /// The end of the mapping interval, in nanoseconds since the UNIX epoch.
    /// </summary>
    public readonly ulong EndTs;

    /// <summary>
    /// The symbology type of <see cref="StypeInSymbol"/>. Undefined wire bytes cast through to an
    /// unnamed value rather than throwing; see <see cref="RawStypeIn"/>.
    /// <see cref="DbnConstants.NullStype"/> on a record upgraded from DBN v1, which does not
    /// carry the field.
    /// </summary>
    public SType StypeIn => (SType)RawStypeIn;

    /// <summary>
    /// The symbology type of <see cref="StypeOutSymbol"/>. Undefined wire bytes cast through to
    /// an unnamed value rather than throwing; see <see cref="RawStypeOut"/>.
    /// <see cref="DbnConstants.NullStype"/> on a record upgraded from DBN v1, which does not
    /// carry the field.
    /// </summary>
    public SType StypeOut => (SType)RawStypeOut;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.SymbolMapping;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<SymbolMappingMsg>();

    /// <summary>
    /// Upgrades a DBN v1 symbol-mapping record to this, the v2/v3 layout. See
    /// <see cref="SymbolMappingMsgV1.UpgradeTo"/>.
    /// </summary>
    /// <param name="old">The record to upgrade.</param>
    internal SymbolMappingMsg(in SymbolMappingMsgV1 old)
    {
        Header = new RecordHeader(
            RType.SymbolMapping,
            WireSize,
            old.Header.PublisherId,
            old.Header.InstrumentId,
            old.Header.TsEvent);

        // Not zero. Zero is SType.InstrumentId, a real symbology type, and claiming it for a
        // record that never carried the field would be a wrong answer rather than a missing one.
        // DbnConstants.NullStype is upstream's default for these fields.
        RawStypeIn = DbnConstants.NullStype;
        RawStypeOut = DbnConstants.NullStype;

        // The v1 symbol fields are 22 bytes and the v2 ones are 71. Copy the old bytes, NUL
        // padding included, and leave the rest of the wider buffers zeroed.
        var stypeInSymbol = default(CStr71);
        old.StypeInSymbol.AsSpan().CopyTo(stypeInSymbol);
        StypeInSymbol = stypeInSymbol;

        var stypeOutSymbol = default(CStr71);
        old.StypeOutSymbol.AsSpan().CopyTo(stypeOutSymbol);
        StypeOutSymbol = stypeOutSymbol;

        StartTs = old.StartTs;
        EndTs = old.EndTs;
    }
}
