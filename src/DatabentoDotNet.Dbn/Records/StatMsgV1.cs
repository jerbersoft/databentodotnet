using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The DBN v1 and v2 layout of <see cref="StatMsg"/>, 64 bytes.
/// </summary>
/// <remarks>
/// One struct covers both versions: upstream's <c>v2</c> module re-exports the v1 struct
/// unchanged, so v1 and v2 statistics records are byte-identical and only v3 differs. The two
/// differences from v3 are that <see cref="Quantity"/> is 32-bit rather than 64-bit — sentinel
/// included, which is what makes <see cref="UpgradeTo"/> more than a widening — and that the
/// trailing reserved block is 6 bytes rather than 18.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct StatMsgV1 : IRecord<StatMsgV1>
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
    /// The value for non-price statistics. <see cref="DbnConstants.UndefStatQuantityV1"/> — not
    /// <see cref="DbnConstants.UndefStatQuantity"/> — when unused.
    /// </summary>
    public readonly int Quantity;

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

    private readonly ReservedBytes6 _reserved;

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
    /// <remarks>
    /// <see cref="TsRecv"/>, not <see cref="RecordHeader.TsEvent"/> — see the remarks on
    /// <see cref="IRecord{TSelf}.IndexTs"/>.
    /// </remarks>
    public ulong IndexTs => TsRecv;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.Statistics;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<StatMsgV1>();

    /// <summary>
    /// Converts this record to the current-version <see cref="StatMsg"/>.
    /// </summary>
    /// <remarks>
    /// A value-level conversion into larger storage, never an in-place reinterpret: the target
    /// is 80 bytes to this record's 64 and its fields sit at different offsets.
    /// <see cref="RecordHeader.Length"/> is recomputed for the new size, and
    /// <see cref="Quantity"/>'s sentinel is translated rather than widened.
    /// </remarks>
    /// <returns>The equivalent v3 record.</returns>
    public StatMsg UpgradeTo() => new(in this);
}
