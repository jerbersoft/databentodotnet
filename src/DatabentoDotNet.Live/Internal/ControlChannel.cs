using System.Buffers;
using System.Text;

namespace DatabentoDotNet.Live.Internal;

/// <summary>
/// The plaintext, <c>\n</c>-terminated line protocol the client and the gateway speak before —
/// and alongside — the DBN record stream.
/// </summary>
/// <remarks>
/// <para>
/// Control lines are always uncompressed. The <c>compression=</c> a client negotiates in its
/// authentication request applies to the DBN stream that follows <c>start_session</c> and to
/// nothing before it, which is why this reads and writes the raw
/// <see cref="System.Net.Sockets.NetworkStream"/> rather than whatever the record path ends up
/// layered on.
/// </para>
/// <para>
/// <b>It reads one byte at a time, and that is not an oversight.</b> The very next thing on the
/// socket after the client's <c>start_session</c> is DBN metadata — possibly inside a zstd frame
/// — so a buffered reader that pulled eight bytes too many while reading the auth response would
/// swallow the front of that metadata with no way to hand it back. Three short lines per session
/// make the cost of the syscalls irrelevant, and the alternative is a buffer that has to be
/// drained into the decoder's own, correctly, on a path nothing routinely exercises.
/// <c>MockLiveGateway</c>'s reader buffers freely because it never reads anything but lines.
/// </para>
/// <para>
/// <b>Cancellation is the caller's problem, not this type's.</b> Authentication and subscription
/// are not cancel-safe (PORTING.md §4): a half-written control line desynchronises the gateway,
/// which then closes the connection. <see cref="LiveClient"/> therefore cancels by tearing the
/// socket down rather than by threading a token into the middle of a write, and passes
/// <see cref="CancellationToken.None"/> here.
/// </para>
/// </remarks>
internal sealed class ControlChannel
{
    /// <summary>
    /// The most a single control line may run to before it is treated as a fault rather than as a
    /// line. Real ones are a few hundred bytes; the longest the client itself ever sends is a
    /// 500-symbol subscription. A gateway that never sends a terminator would otherwise be
    /// answered by growing a buffer until the process dies — at loopback speed, well inside any
    /// handshake budget.
    /// </summary>
    internal const int MaxLineLength = 64 * 1024;

    private const int InitialLineCapacity = 256;

    private readonly Stream _stream;
    private readonly byte[] _single = new byte[1];

    /// <summary>Creates a channel over <paramref name="stream"/>.</summary>
    /// <param name="stream">The socket stream. Not owned: closing it stays the client's job.</param>
    internal ControlChannel(Stream stream) => _stream = stream;

    /// <summary>Reads one line, without its terminator.</summary>
    /// <param name="expecting">
    /// What the caller is waiting for, in words — "the CRAM challenge". It appears in the message
    /// when the gateway goes silent, which is the case where the exception is all anyone has.
    /// </param>
    /// <param name="cancellationToken">Cancels the read. See the remarks on this type.</param>
    /// <returns>The line.</returns>
    /// <exception cref="LiveProtocolException">
    /// The gateway closed the connection mid-line, or sent <see cref="MaxLineLength"/> bytes
    /// without a terminator.
    /// </exception>
    internal async Task<string> ReadLineAsync(string expecting, CancellationToken cancellationToken)
    {
        var line = new ArrayBufferWriter<byte>(InitialLineCapacity);

        while (true)
        {
            var read = await _stream.ReadAsync(_single.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new LiveProtocolException(
                    line.WrittenCount == 0
                        ? $"The live gateway closed the connection while the client was waiting for {expecting}."
                        : $"The live gateway closed the connection {line.WrittenCount} bytes into {expecting}: "
                          + $"'{Decode(line)}'.");
            }

            if (_single[0] == (byte)'\n')
            {
                return Decode(line);
            }

            if (line.WrittenCount == MaxLineLength)
            {
                throw new LiveProtocolException(
                    $"The live gateway sent more than {MaxLineLength} bytes without a terminator while "
                    + $"the client was waiting for {expecting}.");
            }

            line.Write(_single);
        }
    }

    /// <summary>Sends one line, appending the terminator.</summary>
    /// <param name="line">The line, without a terminator.</param>
    /// <param name="cancellationToken">Cancels the write. See the remarks on this type.</param>
    internal async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        // One write, not one per field: the gateway parses a line at a time, and a control message
        // split across TCP segments is a message the gateway can see half of.
        var bytes = Encoding.UTF8.GetBytes(line + '\n');

        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Decode(ArrayBufferWriter<byte> line) => Encoding.UTF8.GetString(line.WrittenSpan);
}
