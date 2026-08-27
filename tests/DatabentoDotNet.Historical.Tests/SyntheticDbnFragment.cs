using System.Buffers.Binary;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Builds a DBN record fragment — records with no metadata block, the shape the vendored
/// <c>.dbn.frag</c> fixtures carry — for <see cref="MockHistoricalGateway"/> to serve as a body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fragment and not a whole DBN stream.</b> This project references no <c>src/</c>
/// project, so there is no <c>MetadataEncoder</c> to call, and hand-writing a metadata block from
/// the specification would put a second, unverified DBN encoder in a test project — one nothing
/// checks and everything downstream would inherit. Nothing here needs one: the gateway serves
/// opaque bytes and asserts nothing about them, and the only property the harness's own tests care
/// about is <em>record framing</em> — that a body cut at the wrong offset ends mid-record.
/// </para>
/// <para>
/// <b>The framing is the documented one.</b> A DBN record opens with a 16-byte header whose first
/// byte is the record's total length in four-byte units, then <c>rtype</c>, <c>publisher_id</c>
/// (u16), <c>instrument_id</c> (u32) and <c>ts_event</c> (u64), all little-endian. Everything after
/// that is the record's own payload, and here it is a byte pattern rather than a real schema: every
/// byte of the stream is distinct within a 256-byte window, so a range served from the wrong offset
/// or a chunk written twice shows up as a mismatch at a nameable position rather than as a length
/// that happens to differ.
/// </para>
/// </remarks>
public static class SyntheticDbnFragment
{
    /// <summary>The size of every record here, in bytes: a 16-byte header and a 16-byte payload.</summary>
    public const int RecordSize = 32;

    /// <summary>The size of a DBN record header, in bytes.</summary>
    public const int HeaderSize = 16;

    /// <summary>The unit the header's length field counts in.</summary>
    public const int LengthMultiplier = 4;

    /// <summary>The <c>rtype</c> every record here claims. Zero is MBO.</summary>
    public const byte RType = 0x00;

    /// <summary>The publisher every record here claims, so a decoded record is traceable to this file.</summary>
    public const ushort PublisherId = 1;

    /// <summary>The instrument every record here claims.</summary>
    public const uint InstrumentId = 1_234;

    /// <summary>The first record's <c>ts_event</c>: 2023-07-04T00:00:00Z, in nanoseconds.</summary>
    public const ulong FirstTsEvent = 1_688_428_800_000_000_000UL;

    /// <summary>
    /// <paramref name="count"/> records, back to back, numbered from zero.
    /// </summary>
    /// <param name="count">How many.</param>
    /// <returns>The fragment, <c><paramref name="count"/> * <see cref="RecordSize"/></c> bytes long.</returns>
    public static byte[] Records(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var fragment = new byte[count * RecordSize];
        for (var index = 0; index < count; index++)
        {
            var record = fragment.AsSpan(index * RecordSize, RecordSize);

            record[0] = RecordSize / LengthMultiplier;
            record[1] = RType;
            BinaryPrimitives.WriteUInt16LittleEndian(record[2..], PublisherId);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], InstrumentId);
            BinaryPrimitives.WriteUInt64LittleEndian(record[8..], FirstTsEvent + (ulong)index);

            for (var offset = HeaderSize; offset < RecordSize; offset++)
            {
                record[offset] = (byte)(((index * (RecordSize - HeaderSize)) + offset - HeaderSize) & 0xFF);
            }
        }

        return fragment;
    }

    /// <summary>
    /// The total length the record starting at <paramref name="record"/> declares, in bytes.
    /// </summary>
    /// <remarks>
    /// The one thing a decoder reads before it knows whether it holds a whole record, and therefore
    /// the thing a mid-record truncation has to be measured against.
    /// </remarks>
    /// <param name="record">The bytes from the start of a record. Only the first is read.</param>
    /// <returns>The declared length in bytes.</returns>
    public static int DeclaredLength(ReadOnlySpan<byte> record) => record[0] * LengthMultiplier;
}
