using System.Text;

namespace DatabentoDotNet.Live.Tests;

/// <summary>
/// Reads <c>\n</c>-terminated control lines off the socket for <see cref="MockLiveGateway"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This one buffers ahead; the client stub deliberately does not.</b> PORTING.md §3 forbids a
/// second buffering layer on the <em>client's</em> read path, because that path reinterprets
/// records in place over the FSM's own buffer. Nothing of the sort applies here: the gateway
/// reads control lines and never reads a byte of DBN, so it can never over-read into binary it
/// was supposed to leave alone. <see cref="StubLiveClient"/> reads its three control lines a byte
/// at a time for exactly the opposite reason — the next thing it reads <em>is</em> binary, and
/// possibly a zstd frame, so an over-read there would be unrecoverable.
/// </para>
/// <para>
/// A missing terminator is a bounded failure rather than an unbounded allocation:
/// <see cref="MaxLineLength"/> is well clear of the longest legitimate line — a 500-symbol
/// subscription chunk at DBN's 71-byte symbol limit is about 36 KiB.
/// </para>
/// </remarks>
internal sealed class GatewayLineReader
{
    /// <summary>The longest line accepted before the reader gives up on ever seeing a <c>\n</c>.</summary>
    internal const int MaxLineLength = 64 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    internal GatewayLineReader(Stream stream) => _stream = stream;

    /// <summary>
    /// Reads one line, without its terminator.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The line, decoded as UTF-8.</returns>
    /// <exception cref="MockGatewayException">
    /// The client closed the connection instead of sending a line, closed it partway through one,
    /// or sent more than <see cref="MaxLineLength"/> bytes without a terminator.
    /// </exception>
    internal async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        // Capacity, not length: GetBuffer() below needs the publicly-visible-buffer constructor.
        using var line = new MemoryStream(256);

        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (_end == 0)
                {
                    throw new MockGatewayException(line.Length == 0
                        ? "The client closed the connection while the gateway was waiting for a control line."
                        : $"The client closed the connection {line.Length} bytes into an unterminated "
                          + $"control line: '{Decode(line)}'.");
                }
            }

            var available = _buffer.AsSpan(_start, _end - _start);
            var terminator = available.IndexOf((byte)'\n');
            var take = terminator < 0 ? available.Length : terminator;

            if (line.Length + take > MaxLineLength)
            {
                throw new MockGatewayException(
                    $"The client sent more than {MaxLineLength} bytes with no line terminator. "
                    + $"It starts: '{Decode(line)}'.");
            }

            line.Write(available[..take]);
            _start += take;

            if (terminator >= 0)
            {
                _start++;
                return Decode(line);
            }
        }
    }

    // UTF-8 rather than ASCII on purpose. The protocol is ASCII, but a line that is not is a line
    // this harness is about to reject, and its message should show what actually arrived rather
    // than a run of '?'.
    private static string Decode(MemoryStream line) =>
        Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
}
