using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A statistic disseminated by a publisher. The record of the <see cref="Schema.Statistics"/>
/// schema; <see cref="StatType"/> says which statistic it carries.
/// </summary>
/// <remarks>
/// This is the DBN v3 layout, 80 bytes. Versions 1 and 2 share a different, 64-byte layout —
/// see <see cref="StatMsgV1"/>. Field order is transcribed from the <c>#[repr(C)]</c> Rust
/// declaration, not from its <c>encode_order</c> attributes.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct StatMsg : IRecord<StatMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>
    /// The capture-server-received timestamp, in nanoseconds since the UNIX epoch. This, not
    /// <see cref="RecordHeader.TsEvent"/>, is the record's index timestamp.
    /// </summary>
    public readonly ulong TsRecv;

    /// <summary>
    /// The reference timestamp of the statistic value, in nanoseconds since the UNIX epoch.
    /// <see cref="DbnConstants.UndefTimestamp"/> when unused.
    /// </summary>
    public readonly ulong TsRef;

    /// <summary>
    /// The value for price statistics, where every 1 unit corresponds to 1e-9.
    /// <see cref="DbnConstants.UndefPrice"/> when unused.
    /// </summary>
    public readonly long Price;

    /// <summary>
    /// The value for non-price statistics. <see cref="DbnConstants.UndefStatQuantity"/> when
    /// unused.
    /// </summary>
    /// <remarks>
    /// 64-bit as of DBN v3; it is 32-bit in <see cref="StatMsgV1"/>, and so is its sentinel.
    /// </remarks>
    public readonly long Quantity;

    /// <summary>The message sequence number assigned at the venue.</summary>
    public readonly uint Sequence;

    /// <summary>
    /// The matching-engine-sending timestamp, in nanoseconds before <see cref="TsRecv"/>.
    /// </summary>
    public readonly int TsInDelta;

    /// <summary>
    /// The raw wire value behind <see cref="StatType"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromStatType(ushort, out StatType)"/> for a checked conversion.
    /// </summary>
    public readonly ushort RawStatType;

    /// <summary>The channel ID assigned by Databento, an integer counting up from zero.</summary>
    public readonly ushort ChannelId;

    /// <summary>
    /// The raw wire byte behind <see cref="UpdateAction"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromStatUpdateAction(byte, out StatUpdateAction)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly byte RawUpdateAction;

    /// <summary>Additional flags associated with certain statistic types.</summary>
    public readonly byte StatFlags;

    private readonly ReservedBytes18 _reserved;

    /// <summary>
    /// The statistic this record carries. Undefined wire values cast through to an unnamed value
    /// rather than throwing; see <see cref="RawStatType"/>.
    /// </summary>
    public StatType StatType => (StatType)RawStatType;

    /// <summary>
    /// Whether the statistic is newly added or deleted. Undefined wire bytes cast through to an
    /// unnamed value rather than throwing; see <see cref="RawUpdateAction"/>.
    /// </summary>
    public StatUpdateAction UpdateAction => (StatUpdateAction)RawUpdateAction;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.Statistics;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<StatMsg>();

    /// <summary>
    /// Upgrades a DBN v1 or v2 statistics record to this, the v3 layout. See
    /// <see cref="StatMsgV1.UpgradeTo"/>.
    /// </summary>
    /// <param name="old">The record to upgrade.</param>
    internal StatMsg(in StatMsgV1 old)
    {
        Header = new RecordHeader(
            RType.Statistics,
            WireSize,
            old.Header.PublisherId,
            old.Header.InstrumentId,
            old.Header.TsEvent);
        TsRecv = old.TsRecv;
        TsRef = old.TsRef;
        Price = old.Price;

        // The sentinel is translated, not widened. int.MaxValue widened is the literal quantity
        // 2,147,483,647 — a perfectly plausible number in a market-data feed, and one that no
        // "did it decode" test would ever flag.
        Quantity = old.Quantity == DbnConstants.UndefStatQuantityV1
            ? DbnConstants.UndefStatQuantity
            : old.Quantity;

        Sequence = old.Sequence;
        TsInDelta = old.TsInDelta;
        RawStatType = old.RawStatType;
        ChannelId = old.ChannelId;
        RawUpdateAction = old.RawUpdateAction;
        StatFlags = old.StatFlags;
        _reserved = default;
    }
}
