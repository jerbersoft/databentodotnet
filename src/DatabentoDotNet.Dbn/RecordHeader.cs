using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The 16-byte header that begins every DBN record.
/// </summary>
/// <remarks>
/// Layout must match the <c>#[repr(C)]</c> Rust definition byte for byte; it is reinterpreted
/// directly over the read buffer rather than parsed field by field.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RecordHeader
{
    /// <summary>
    /// Length of the whole record in 32-bit words. Use <see cref="SizeInBytes"/> for a byte count.
    /// </summary>
    public readonly byte Length;

    /// <summary>
    /// The record type. Values <c>0x00..0x0F</c> encode market-by-price book depth, so
    /// <c>0x00</c>, <c>0x01</c> and <c>0x0A</c> are depths 0, 1 and 10 rather than arbitrary tags.
    /// </summary>
    public readonly byte RType;

    /// <summary>Publisher ID assigned by Databento, denoting the dataset and venue.</summary>
    public readonly ushort PublisherId;

    /// <summary>Numeric instrument ID.</summary>
    public readonly uint InstrumentId;

    /// <summary>
    /// Matching-engine-received timestamp, in nanoseconds since the UNIX epoch.
    /// </summary>
    /// <remarks>
    /// Kept as raw nanoseconds because <see cref="DateTime"/> resolves only to 100 ns and would
    /// silently discard precision.
    /// </remarks>
    public readonly ulong TsEvent;

    /// <summary>The record's total length in bytes.</summary>
    public int SizeInBytes => Length * DbnConstants.RecordLengthMultiplier;
}
