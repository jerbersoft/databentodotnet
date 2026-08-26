using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A non-error message from the Databento Live Subscription Gateway, also used for heartbeats.
/// </summary>
/// <remarks>
/// This is the DBN v2 layout, unchanged in v3, at 320 bytes. Version 1 is 80 bytes and carries
/// only the message text — see <see cref="SystemMsgV1"/>. The record has no <c>ts_recv</c>, so
/// its index timestamp is <see cref="RecordHeader.TsEvent"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SystemMsg : IRecord<SystemMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The message from the Databento gateway.</summary>
    public readonly CStr303 Msg;

    /// <summary>
    /// The raw wire byte behind <see cref="Code"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromSystemCode(byte, out SystemCode)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawCode;

    /// <summary>
    /// The type of system message. Undefined wire bytes cast through to an unnamed value rather
    /// than throwing; see <see cref="RawCode"/>. <see cref="SystemCode.Unset"/> on a record
    /// upgraded from DBN v1 whose message matched none of the known texts.
    /// </summary>
    public SystemCode Code => (SystemCode)RawCode;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.System;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<SystemMsg>();

    /// <summary>
    /// Upgrades a DBN v1 system record to this, the v2/v3 layout. See
    /// <see cref="SystemMsgV1.UpgradeTo"/>.
    /// </summary>
    /// <param name="old">The record to upgrade.</param>
    internal SystemMsg(in SystemMsgV1 old)
    {
        Header = new RecordHeader(
            RType.System,
            WireSize,
            old.Header.PublisherId,
            old.Header.InstrumentId,
            old.Header.TsEvent);

        // The v1 field is 64 bytes and the v2 field is 303. Copy the old bytes, NUL padding
        // included, and leave the rest of the wider buffer zeroed.
        var msg = default(CStr303);
        old.Msg.AsSpan().CopyTo(msg);
        Msg = msg;

        // v1 has no code field, so the code is recovered from the message text — this is
        // upstream's own v1 -> v2 inference, texts included. Anything unrecognised stays Unset.
        RawCode = (byte)InferCode(old.Msg.AsTextSpan());
    }

    private static SystemCode InferCode(ReadOnlySpan<byte> msg)
    {
        // Equality, not a prefix: upstream's v1 is_heartbeat() compares the whole message
        // against this exact text, which is all a v1 record has to identify a heartbeat by.
        if (msg.SequenceEqual("Heartbeat"u8))
        {
            return SystemCode.Heartbeat;
        }

        if (msg.StartsWith("End of interval for "u8))
        {
            return SystemCode.EndOfInterval;
        }

        if (msg.StartsWith("Subscription request "u8) && msg.EndsWith(" succeeded"u8))
        {
            return SystemCode.SubscriptionAck;
        }

        if (msg.StartsWith("Warning: slow reading"u8))
        {
            return SystemCode.SlowReaderWarning;
        }

        if (msg.StartsWith("Finished "u8) && msg.EndsWith(" replay"u8))
        {
            return SystemCode.ReplayCompleted;
        }

        return SystemCode.Unset;
    }
}
