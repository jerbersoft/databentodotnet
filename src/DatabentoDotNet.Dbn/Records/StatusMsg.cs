using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// An exchange status change. The record of the <see cref="Schema.Status"/> schema.
/// </summary>
/// <remarks>
/// The three enum-valued fields here are 16-bit, not the single ASCII byte used by
/// <c>Action</c> and <c>Side</c> elsewhere, while the three tri-state flags are single ASCII
/// bytes. Field order is transcribed from the <c>#[repr(C)]</c> Rust declaration.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct StatusMsg : IRecord<StatusMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>
    /// The capture-server-received timestamp, in nanoseconds since the UNIX epoch. This, not
    /// <see cref="RecordHeader.TsEvent"/>, is the record's index timestamp.
    /// </summary>
    public readonly ulong TsRecv;

    /// <summary>
    /// The raw wire value behind <see cref="Action"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromStatusAction(ushort, out StatusAction)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly ushort RawAction;

    /// <summary>
    /// The raw wire value behind <see cref="Reason"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromStatusReason(ushort, out StatusReason)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly ushort RawReason;

    /// <summary>
    /// The raw wire value behind <see cref="TradingEvent"/>. Not validated on decode — pass it
    /// to <see cref="EnumValues.TryFromTradingEvent(ushort, out TradingEvent)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly ushort RawTradingEvent;

    /// <summary>
    /// The raw wire byte behind <see cref="IsTrading"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromTriState(byte, out TriState)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawIsTrading;

    /// <summary>
    /// The raw wire byte behind <see cref="IsQuoting"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromTriState(byte, out TriState)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawIsQuoting;

    /// <summary>
    /// The raw wire byte behind <see cref="IsShortSellRestricted"/>. Not validated on decode —
    /// pass it to <see cref="EnumValues.TryFromTriState(byte, out TriState)"/> for a checked
    /// conversion.
    /// </summary>
    public readonly byte RawIsShortSellRestricted;

    private readonly ReservedBytes7 _reserved;

    /// <summary>
    /// The type of status change. Undefined wire values cast through to an unnamed value rather
    /// than throwing; see <see cref="RawAction"/>.
    /// </summary>
    public StatusAction Action => (StatusAction)RawAction;

    /// <summary>
    /// The reason for the status change. Undefined wire values cast through to an unnamed value
    /// rather than throwing; see <see cref="RawReason"/>.
    /// </summary>
    public StatusReason Reason => (StatusReason)RawReason;

    /// <summary>
    /// Further information about the status change and its effect on trading. Undefined wire
    /// values cast through to an unnamed value rather than throwing; see
    /// <see cref="RawTradingEvent"/>.
    /// </summary>
    public TradingEvent TradingEvent => (TradingEvent)RawTradingEvent;

    /// <summary>Whether the instrument is currently trading, as its raw ASCII character.</summary>
    public char IsTradingChar => (char)RawIsTrading;

    /// <summary>
    /// Whether the instrument is currently trading. Undefined wire bytes cast through to an
    /// unnamed value rather than throwing; see <see cref="RawIsTrading"/>.
    /// </summary>
    public TriState IsTrading => (TriState)RawIsTrading;

    /// <summary>Whether the instrument is currently quoting, as its raw ASCII character.</summary>
    public char IsQuotingChar => (char)RawIsQuoting;

    /// <summary>
    /// Whether the instrument is currently quoting. Undefined wire bytes cast through to an
    /// unnamed value rather than throwing; see <see cref="RawIsQuoting"/>.
    /// </summary>
    public TriState IsQuoting => (TriState)RawIsQuoting;

    /// <summary>
    /// Whether the instrument is subject to a short-sell restriction, as its raw ASCII
    /// character.
    /// </summary>
    public char IsShortSellRestrictedChar => (char)RawIsShortSellRestricted;

    /// <summary>
    /// Whether the instrument is subject to a short-sell restriction. Undefined wire bytes cast
    /// through to an unnamed value rather than throwing; see
    /// <see cref="RawIsShortSellRestricted"/>.
    /// </summary>
    public TriState IsShortSellRestricted => (TriState)RawIsShortSellRestricted;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.Status;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<StatusMsg>();
}
