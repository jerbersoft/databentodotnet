using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// A record plus the live gateway's send timestamp: eight more bytes on the wire, appended after
/// the record itself.
/// </summary>
/// <typeparam name="T">The wrapped record type.</typeparam>
/// <remarks>
/// <para>
/// A live session can be configured to have the gateway stamp each record with the time it sent
/// it. When it is, every record on that stream is eight bytes longer than its own struct, and
/// the DBN metadata says so — the wrapper is a property of the stream, not of the record type.
/// </para>
/// <para>
/// Every record's size is a multiple of eight, so <see cref="TsOut"/> lands 8-byte aligned with
/// no padding between it and the record, whatever <typeparamref name="T"/> is.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct WithTsOut<T>
    where T : unmanaged, IRecord<T>
{
    /// <summary>The wrapped record.</summary>
    public readonly T Record;

    /// <summary>
    /// The live gateway send timestamp, in nanoseconds since the UNIX epoch.
    /// </summary>
    public readonly ulong TsOut;

    /// <summary>
    /// Wraps a record, recomputing its header length to account for the extra eight bytes.
    /// </summary>
    /// <param name="record">The record to wrap.</param>
    /// <param name="tsOut">The live gateway send timestamp, in nanoseconds since the UNIX epoch.</param>
    /// <remarks>
    /// This is the one construction path in the codec that does not get its length right for
    /// free. Everywhere else <c>hd.length</c> either arrives from the wire already correct or is
    /// computed from the target struct's own size; here the record is correct for itself and
    /// wrong by eight bytes for the stream it is about to go into, and a wrong length
    /// desynchronises every record after it.
    /// </remarks>
    public WithTsOut(T record, ulong tsOut)
    {
        // `hd.length` is the first byte of `RecordHeader`, which is the first field of every
        // record struct, so it is the first byte of the record's own bytes. Writing it directly
        // is what lets this work for any T: RecordHeader is a readonly struct, and T is a type
        // parameter, so there is no typed path to that field from here. RecordsDeclareTheir-
        // HeaderFirst in the layout tests is what pins the assumption.
        var wrapped = record;
        Unsafe.As<T, byte>(ref wrapped) =
            checked((byte)(Unsafe.SizeOf<WithTsOut<T>>() / DbnConstants.RecordLengthMultiplier));

        Record = wrapped;
        TsOut = tsOut;
    }
}
