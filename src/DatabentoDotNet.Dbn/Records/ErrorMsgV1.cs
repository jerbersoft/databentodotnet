using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// The DBN v1 layout of <see cref="ErrorMsg"/>, 80 bytes.
/// </summary>
/// <remarks>
/// Version 1 carries the message text and nothing else: the <c>code</c> and <c>is_last</c>
/// fields arrived in v2, and the message field itself is 64 bytes rather than 302.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ErrorMsgV1 : IRecord<ErrorMsgV1>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The error message.</summary>
    public readonly CStr64 Err;

    /// <inheritdoc/>
    /// <remarks>
    /// This record has no <c>ts_recv</c>, so its index timestamp is the header's
    /// <see cref="RecordHeader.TsEvent"/> — upstream's default, not an override.
    /// </remarks>
    public ulong IndexTs => Header.TsEvent;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.Error;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<ErrorMsgV1>();

    /// <summary>
    /// Converts this record to the current-version <see cref="ErrorMsg"/>.
    /// </summary>
    /// <remarks>
    /// A value-level conversion into larger storage, never an in-place reinterpret.
    /// <see cref="RecordHeader.Length"/> is recomputed for the new size, the message is copied
    /// into the wider buffer, and the <see cref="ErrorMsg.Code"/> this version does not carry is
    /// inferred from the message text exactly as upstream infers it. <see cref="ErrorMsg"/>'s
    /// layout is identical in v2 and v3, so this one conversion serves both upgrade policies.
    /// </remarks>
    /// <returns>The equivalent current-version record.</returns>
    public ErrorMsg UpgradeTo() => new(in this);
}
