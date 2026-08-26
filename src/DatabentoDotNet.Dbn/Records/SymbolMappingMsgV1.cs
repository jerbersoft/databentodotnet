using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The DBN v1 layout of <see cref="SymbolMappingMsg"/>, 80 bytes.
/// </summary>
/// <remarks>
/// Version 1 carries the two symbols and the interval, but neither symbology type: the
/// <c>stype_in</c> and <c>stype_out</c> fields arrived in v2. The symbol fields are 22 bytes
/// rather than 71, and a four-byte reserved block sits between them and
/// <see cref="StartTs"/> to keep that field 8-byte aligned.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SymbolMappingMsgV1 : IRecord<SymbolMappingMsgV1>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The input symbol.</summary>
    public readonly CStr22 StypeInSymbol;

    /// <summary>The output symbol.</summary>
    public readonly CStr22 StypeOutSymbol;

    private readonly ReservedBytes4 _dummy;

    /// <summary>
    /// The start of the mapping interval, in nanoseconds since the UNIX epoch.
    /// </summary>
    public readonly ulong StartTs;

    /// <summary>
    /// The end of the mapping interval, in nanoseconds since the UNIX epoch.
    /// </summary>
    public readonly ulong EndTs;

    /// <inheritdoc/>
    /// <remarks>
    /// This record has no <c>ts_recv</c>, so its index timestamp is the header's
    /// <see cref="RecordHeader.TsEvent"/> — upstream's default, not an override.
    /// </remarks>
    public ulong IndexTs => Header.TsEvent;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.SymbolMapping;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<SymbolMappingMsgV1>();

    /// <summary>
    /// Converts this record to the current-version <see cref="SymbolMappingMsg"/>.
    /// </summary>
    /// <remarks>
    /// A value-level conversion into larger storage, never an in-place reinterpret.
    /// <see cref="RecordHeader.Length"/> is recomputed for the new size, both symbols are copied
    /// into the wider buffers, and the two symbology-type fields this version does not carry are
    /// set to <see cref="DbnConstants.NullStype"/> rather than zero — zero is
    /// <see cref="SType.InstrumentId"/>, a real answer.
    /// <see cref="SymbolMappingMsg"/>'s layout is identical in v2 and v3, so this one conversion
    /// serves both upgrade policies.
    /// </remarks>
    /// <returns>The equivalent current-version record.</returns>
    public SymbolMappingMsg UpgradeTo() => new(in this);
}
