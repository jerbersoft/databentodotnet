using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The DBN v1 layout of <see cref="SystemMsg"/>, 80 bytes.
/// </summary>
/// <remarks>
/// Version 1 carries the message text and nothing else: the <c>code</c> field arrived in v2, so
/// a v1 heartbeat is recognised only by its message text being exactly <c>Heartbeat</c>. The
/// message field is 64 bytes rather than 303.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SystemMsgV1 : IRecord<SystemMsgV1>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The message from the Databento gateway.</summary>
    public readonly CStr64 Msg;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.System;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<SystemMsgV1>();

    /// <summary>
    /// Converts this record to the current-version <see cref="SystemMsg"/>.
    /// </summary>
    /// <remarks>
    /// A value-level conversion into larger storage, never an in-place reinterpret.
    /// <see cref="RecordHeader.Length"/> is recomputed for the new size, the message is copied
    /// into the wider buffer, and the <see cref="SystemMsg.Code"/> this version does not carry is
    /// inferred from the message text exactly as upstream infers it. <see cref="SystemMsg"/>'s
    /// layout is identical in v2 and v3, so this one conversion serves both upgrade policies.
    /// </remarks>
    /// <returns>The equivalent current-version record.</returns>
    public SystemMsg UpgradeTo() => new(in this);
}
