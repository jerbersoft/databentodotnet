using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DatabentoDotNet.Dbn;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Builds <see cref="MboMsg"/> records to replay through <see cref="MockLiveGateway"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why MBO, and why synthetic.</b> MBO is the densest schema DBN defines — every order-book
/// event, not a summary of them — which makes it the hardest case for the per-record allocation
/// claim M2's definition of done rests on. It is also a schema this account holds no live license
/// for (ROADMAP.md §4, via
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/27">#27</see>), so it is
/// manufactured here rather than subscribed to. That gives up nothing: allocation is a property
/// of the code path, and nothing between the socket and <see cref="Dbn.RecordRef"/> can tell a
/// real gateway from this.
/// </para>
/// <para>
/// <b>Records are built byte by byte because they have no public constructor.</b> Every field is
/// <see langword="readonly"/> and only ever arrives from a reinterpret over a buffer, so a test
/// that needs one builds it the way the wire does. Storage is <see langword="ulong"/>[] so the
/// bytes start 8-byte aligned, matching every other record-building helper in this repo.
/// </para>
/// </remarks>
public static class SyntheticMbo
{
    /// <summary>The publisher every record here claims, so a decoded record is traceable to this file.</summary>
    public const ushort PublisherId = 1;

    /// <summary>The instrument every record here claims.</summary>
    public const uint InstrumentId = 1_234;

    /// <summary>The first record's <c>ts_recv</c>: 2023-07-04T00:00:00Z, in nanoseconds.</summary>
    public const ulong FirstTsRecv = 1_688_428_800_000_000_000UL;

    /// <summary>
    /// One record, with every field distinct enough that a byte-for-byte comparison would notice a
    /// field written at the wrong offset.
    /// </summary>
    /// <param name="sequence">
    /// The venue sequence number, and the seed for the fields that vary: <c>ts_recv</c>,
    /// <c>order_id</c> and <c>price</c> are derived from it, so no two records in a run are equal
    /// and a decoder that returned the same record twice would be caught.
    /// </param>
    /// <returns>The record.</returns>
    public static MboMsg Record(uint sequence)
    {
        var storage = new ulong[MboMsg.WireSize / sizeof(ulong)];
        var bytes = MemoryMarshal.AsBytes(storage.AsSpan());

        bytes[0] = checked((byte)(MboMsg.WireSize / DbnConstants.RecordLengthMultiplier));
        bytes[1] = (byte)RType.Mbo;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], PublisherId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], InstrumentId);

        // ts_event one nanosecond before ts_recv, as a real feed's would be. MBO indexes on
        // ts_recv, so a record that mixed the two up would still look plausible — which is
        // exactly why they differ here.
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], FirstTsRecv + sequence - 1);

        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], 900_000UL + sequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], 100_000_000_000L + sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[32..], 25);

        bytes[36] = 0;                          // flags
        bytes[37] = 3;                          // channel_id
        bytes[38] = (byte)DatabentoDotNet.Dbn.Action.Add;
        bytes[39] = (byte)(sequence % 2 == 0 ? Side.Bid : Side.Ask);

        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], FirstTsRecv + sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], 17);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[52..], sequence);

        return MemoryMarshal.Read<MboMsg>(bytes);
    }

    /// <summary>
    /// <paramref name="count"/> records, numbered from one.
    /// </summary>
    /// <param name="count">How many.</param>
    /// <returns>The records, in sequence order.</returns>
    public static MboMsg[] Records(int count)
    {
        var records = new MboMsg[count];
        for (var i = 0; i < count; i++)
        {
            records[i] = Record((uint)(i + 1));
        }

        return records;
    }
}
