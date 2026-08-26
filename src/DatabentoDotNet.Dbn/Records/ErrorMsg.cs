using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DatabentoDotNet.Dbn;

/// <summary>
/// An error from the Databento Live Subscription Gateway.
/// </summary>
/// <remarks>
/// This is the DBN v2 layout, unchanged in v3, at 320 bytes. Version 1 is 80 bytes and carries
/// only the message text — see <see cref="ErrorMsgV1"/>. The record has no <c>ts_recv</c>, so
/// its index timestamp is <see cref="RecordHeader.TsEvent"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct ErrorMsg : IRecord<ErrorMsg>
{
    /// <summary>The common header.</summary>
    public readonly RecordHeader Header;

    /// <summary>The error message.</summary>
    public readonly CStr302 Err;

    /// <summary>
    /// The raw wire byte behind <see cref="Code"/>. Not validated on decode — pass it to
    /// <see cref="EnumValues.TryFromErrorCode(byte, out ErrorCode)"/> for a checked conversion.
    /// </summary>
    public readonly byte RawCode;

    /// <summary>
    /// Non-zero on the last error of a batch of related errors. Errors are sometimes sent
    /// together.
    /// </summary>
    public readonly byte IsLast;

    /// <summary>
    /// The error code. Undefined wire bytes cast through to an unnamed value rather than
    /// throwing; see <see cref="RawCode"/>. <see cref="ErrorCode.Unset"/> on a record upgraded
    /// from DBN v1 whose message matched none of the known texts.
    /// </summary>
    public ErrorCode Code => (ErrorCode)RawCode;

    /// <inheritdoc/>
    public static bool HasRType(RType rtype) => rtype == RType.Error;

    /// <inheritdoc/>
    public static int WireSize => Unsafe.SizeOf<ErrorMsg>();

    /// <summary>
    /// Upgrades a DBN v1 error record to this, the v2/v3 layout. See
    /// <see cref="ErrorMsgV1.UpgradeTo"/>.
    /// </summary>
    /// <param name="old">The record to upgrade.</param>
    internal ErrorMsg(in ErrorMsgV1 old)
    {
        Header = new RecordHeader(
            RType.Error,
            WireSize,
            old.Header.PublisherId,
            old.Header.InstrumentId,
            old.Header.TsEvent);

        // The v1 field is 64 bytes and the v2 field is 302. Copy the old bytes, NUL padding
        // included, and leave the rest of the wider buffer zeroed.
        var err = default(CStr302);
        old.Err.AsSpan().CopyTo(err);
        Err = err;

        // v1 has no code field, so the code is recovered from the message text — this is
        // upstream's own v1 -> v2 inference, texts included. Anything unrecognised stays Unset.
        RawCode = (byte)InferCode(old.Err.AsTextSpan());

        // Not zero: upstream's ErrorMsg default for is_last is byte.MaxValue, and an upgraded v1
        // record carries no batching information at all.
        IsLast = byte.MaxValue;
    }

    private static ErrorCode InferCode(ReadOnlySpan<byte> err)
    {
        if (err.SequenceEqual("User or API key deactivated"u8))
        {
            return ErrorCode.ApiKeyDeactivated;
        }

        if (err.SequenceEqual("User has reached their open connection limit"u8))
        {
            return ErrorCode.ConnectionLimitExceeded;
        }

        if (err.StartsWith("Failed to resolve symbol"u8))
        {
            return ErrorCode.SymbolResolutionFailed;
        }

        if (err.SequenceEqual("Internal error"u8))
        {
            return ErrorCode.InternalError;
        }

        if (err.StartsWith("Slow client detected for "u8))
        {
            return ErrorCode.SkippedRecordsAfterSlowReading;
        }

        return ErrorCode.Unset;
    }
}
