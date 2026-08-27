using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DatabentoDotNet.Dbn;

// System.Text.Encoding and DatabentoDotNet.Dbn.Encoding collide by simple name, as they do in
// MockLiveGateway and in LiveClient itself.
using Encoding = System.Text.Encoding;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Builds <see cref="SystemMsg"/> records — heartbeats above all — to replay through
/// <see cref="MockLiveGateway"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A heartbeat is an ordinary record, and that is the thing worth asserting.</b> The gateway
/// does not send it as a control frame: it arrives in the DBN stream, framed like every other
/// record, and a client that expected a separate channel for it would either miss it or
/// desynchronise on it. So it is built here exactly as the wire builds one and sent down the same
/// <see cref="MockLiveGateway.SendRecordAsync{T}"/> as a trade. PORTING.md §4.
/// </para>
/// <para>
/// Built byte by byte for the reason <see cref="SyntheticMbo"/> gives: every field is
/// <see langword="readonly"/> and the only constructor is the internal v1 upgrade, so a test that
/// needs a record builds it the way the wire does.
/// </para>
/// </remarks>
public static class SyntheticSystemMsg
{
    /// <summary>
    /// The message text a heartbeat carries, matching upstream's <c>SystemMsg::HEARTBEAT</c>.
    /// </summary>
    /// <remarks>
    /// Upstream's <c>is_heartbeat()</c> falls back to comparing this text when the code is
    /// unreadable — which is how a DBN v1 heartbeat, whose record has no code field at all, is
    /// recognised. Sending the text as well as the code is what a real gateway does.
    /// </remarks>
    public const string HeartbeatText = "Heartbeat";

    /// <summary>The offset of the <c>msg</c> field: straight after the 16-byte header.</summary>
    private const int MsgOffset = 16;

    /// <summary>The offset of the <c>code</c> byte: the last byte of the record.</summary>
    private const int CodeOffset = MsgOffset + CStr303.Length;

    /// <summary>
    /// A gateway heartbeat, stamped <paramref name="tsEvent"/>.
    /// </summary>
    /// <param name="tsEvent">
    /// The event timestamp, in nanoseconds since the UNIX epoch. It is a
    /// <see cref="SystemMsg"/>'s index timestamp too — the record carries no <c>ts_recv</c>.
    /// </param>
    /// <returns>The record.</returns>
    public static SystemMsg Heartbeat(ulong tsEvent) =>
        Record(tsEvent, SystemCode.Heartbeat, HeartbeatText);

    /// <summary>
    /// A system message with any code and text.
    /// </summary>
    /// <param name="tsEvent">The event timestamp, in nanoseconds since the UNIX epoch.</param>
    /// <param name="code">The system code.</param>
    /// <param name="msg">
    /// The message text, which must fit in <see cref="CStr303.Length"/> bytes including its NUL
    /// terminator.
    /// </param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentException"><paramref name="msg"/> does not fit.</exception>
    public static SystemMsg Record(ulong tsEvent, SystemCode code, string msg)
    {
        ArgumentNullException.ThrowIfNull(msg);

        var storage = new ulong[SystemMsg.WireSize / sizeof(ulong)];
        var bytes = MemoryMarshal.AsBytes(storage.AsSpan());

        bytes[0] = checked((byte)(SystemMsg.WireSize / DbnConstants.RecordLengthMultiplier));
        bytes[1] = (byte)RType.System;

        // Publisher and instrument are zero on a gateway message: it is about the session, not
        // about an instrument on a venue. Upstream's SystemMsg::heartbeat passes 0 for both.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[2..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], tsEvent);

        var written = Encoding.UTF8.GetBytes(msg, bytes.Slice(MsgOffset, CStr303.Length - 1));
        if (written != Encoding.UTF8.GetByteCount(msg))
        {
            throw new ArgumentException(
                $"'{msg}' does not fit in a {CStr303.Length}-byte field with its NUL terminator.",
                nameof(msg));
        }

        // The rest of the field stays zero, which is the NUL terminator and the padding after it.
        bytes[CodeOffset] = (byte)code;

        return MemoryMarshal.Read<SystemMsg>(bytes);
    }
}
