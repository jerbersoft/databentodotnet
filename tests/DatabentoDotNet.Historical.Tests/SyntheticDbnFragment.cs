using System.Buffers.Binary;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// Builds a body of DBN-<em>framed</em> records for <see cref="MockHistoricalGateway"/> to serve:
/// records with no metadata block, the shape the vendored <c>.dbn.frag</c> fixtures carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are not real records, and they are deliberately not pretending to be.</b> What is
/// borrowed from DBN is the 16-byte record header — first byte the record's total length in
/// four-byte units, then <c>rtype</c>, <c>publisher_id</c> (u16), <c>instrument_id</c> (u32) and
/// <c>ts_event</c> (u64), little-endian — and nothing else. Everything past it is a byte pattern,
/// and <see cref="RType"/> is a value DBN assigns to no record type at all, so no record here
/// claims a schema whose layout could contradict its length. The gateway serves opaque bytes and
/// asserts nothing about them; the only properties the harness's own tests need are <em>framing</em>
/// — that a body cut at the wrong offset ends mid-record — and <em>position</em>, that a tail served
/// from the wrong offset is visibly the wrong bytes.
/// </para>
/// <para>
/// <b>Position comes from the payload ramp, not from the headers.</b> The 16-byte payloads carry a
/// counter that runs unbroken across record boundaries, so every payload byte in a fragment of up
/// to 16 records is distinct and a range served one byte out mismatches at a nameable position
/// rather than merely differing in length. The header bytes have no such property — bytes 0..7 are
/// byte-identical in every record, and only <c>ts_event</c> varies — which is why the ramp is what
/// the offset assertions actually rest on.
/// </para>
/// <para>
/// <b>This is a fragment, and a metadata block should not be added to it.</b> An issue that needs a
/// client to decode a <em>whole</em> DBN stream off this harness should serve one of the vendored
/// fixtures in <c>tests/DatabentoDotNet.Dbn.Tests/Data/</c> through
/// <see cref="MockHistoricalResponse.Binary"/>, which already takes arbitrary bytes and needs no
/// change to do it. Those files are Databento's bytes. A metadata block this repo produced —
/// hand-written from the specification, or encoded by <c>MetadataEncoder</c> — would put our own
/// reading of the format on both sides of the test, where a misreading agrees with itself and
/// nothing catches it.
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

    /// <summary>
    /// The <c>rtype</c> every record here carries: <c>0xFF</c>, which DBN assigns to no record type.
    /// </summary>
    /// <remarks>
    /// Chosen because it is unassigned, not in spite of it. A real discriminant would pair a
    /// concrete layout — and therefore a concrete size — with a length field saying
    /// <see cref="RecordSize"/>, and any two of those three that disagreed would make this body a
    /// small lie for every issue downstream to build on. An rtype no decoder recognises says what is
    /// true: these bytes exist to be transported, not decoded.
    /// </remarks>
    public const byte RType = 0xFF;

    /// <summary>The <c>publisher_id</c> every record here carries.</summary>
    public const ushort PublisherId = 1;

    /// <summary>The <c>instrument_id</c> every record here carries.</summary>
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
