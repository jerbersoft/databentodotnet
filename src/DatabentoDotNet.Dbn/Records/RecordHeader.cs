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
    /// Builds a header for a record of <paramref name="sizeInBytes"/> bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the version-upgrade conversions need this. An upgraded record is a different, larger
    /// struct than the one it came from, so its <see cref="Length"/> must be recomputed; copying
    /// the old header wholesale is exactly the bug this constructor exists to make impossible.
    /// It stays <see langword="internal"/> because records are otherwise only ever read from a
    /// buffer, never constructed.
    /// </para>
    /// <para>
    /// This is the primitive <see cref="For{T}"/> is built on; a conversion should call
    /// <see cref="For{T}"/> rather than this constructor directly, so the size always comes from
    /// the target type rather than from a value the caller had to get right by hand.
    /// </para>
    /// </remarks>
    /// <param name="rtype">The record type of the record this header will begin.</param>
    /// <param name="sizeInBytes">
    /// The size of the whole record in bytes. Must be a multiple of
    /// <see cref="DbnConstants.RecordLengthMultiplier"/> and fit in a byte once divided by it —
    /// which every DBN record does, the largest being 528 bytes, or 132 words.
    /// </param>
    /// <param name="publisherId">The publisher ID, carried over unchanged.</param>
    /// <param name="instrumentId">The instrument ID, carried over unchanged.</param>
    /// <param name="tsEvent">The event timestamp, carried over unchanged.</param>
    internal RecordHeader(
        RType rtype,
        int sizeInBytes,
        ushort publisherId,
        uint instrumentId,
        ulong tsEvent)
    {
        Length = checked((byte)(sizeInBytes / DbnConstants.RecordLengthMultiplier));
        RType = (byte)rtype;
        PublisherId = publisherId;
        InstrumentId = instrumentId;
        TsEvent = tsEvent;
    }

    /// <summary>
    /// Builds a header for a record of type <typeparamref name="T"/>, taking <see cref="Length"/>
    /// from <see cref="IRecord{TSelf}.WireSize"/> rather than a hand-passed byte count. This is
    /// upstream's <c>RecordHeader::new::&lt;R&gt;</c>: the size comes from the type parameter, so
    /// passing the wrong size — in particular the source record's size, rather than the target's
    /// — is not even expressible.
    /// </summary>
    /// <typeparam name="T">The record type this header will begin.</typeparam>
    /// <param name="rtype">The record type of the record this header will begin.</param>
    /// <param name="publisherId">The publisher ID, carried over unchanged.</param>
    /// <param name="instrumentId">The instrument ID, carried over unchanged.</param>
    /// <param name="tsEvent">The event timestamp, carried over unchanged.</param>
    /// <returns>A header whose <see cref="Length"/> matches <typeparamref name="T"/>'s size.</returns>
    internal static RecordHeader For<T>(
        RType rtype,
        ushort publisherId,
        uint instrumentId,
        ulong tsEvent)
        where T : unmanaged, IRecord<T>
        => new(rtype, T.WireSize, publisherId, instrumentId, tsEvent);

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
