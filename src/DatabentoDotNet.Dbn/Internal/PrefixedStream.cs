namespace DatabentoDotNet.Dbn.Internal;

/// <summary>
/// A read-only stream that serves a small prefix of already-read bytes before delegating to an
/// inner stream — the .NET stand-in for Rust's <c>BufRead::fill_buf()</c> peek.
/// </summary>
/// <remarks>
/// <para>
/// Detecting whether a DBN stream is Zstandard-compressed means looking at its first four bytes,
/// and <em>both</em> answers need those bytes afterwards: the compressed branch because the frame
/// magic is part of the frame, the uncompressed branch because those bytes are the <c>DBN</c>
/// magic. Upstream sniffs through <c>fill_buf()</c>, which peeks into a <c>BufRead</c>'s internal
/// buffer without advancing the read cursor (<c>decode/dyn_reader.rs:75-87</c>). A
/// <see cref="Stream"/> has no equivalent and may not be seekable, so the bytes are read and then
/// handed back at the front of this wrapper. Either way no byte is lost, which is the property
/// that matters: a detection that consumed the magic would break the raw-DBN path outright.
/// </para>
/// <para>
/// Deliberately minimal — no seeking, no writing, no length. It exists to be read once, in order.
/// </para>
/// </remarks>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private int _prefixPosition;

    public PrefixedStream(byte[] prefix, Stream inner, bool leaveOpen)
    {
        _prefix = prefix;
        _inner = inner;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var remaining = _prefix.Length - _prefixPosition;
        if (remaining > 0)
        {
            // Serve only what is left of the prefix, never mixing it with a read from the inner
            // stream: a Stream.Read is allowed to return fewer bytes than asked for, so a short
            // return here is correct rather than a shortfall to paper over.
            var take = Math.Min(remaining, buffer.Length);
            _prefix.AsSpan(_prefixPosition, take).CopyTo(buffer);
            _prefixPosition += take;
            return take;
        }

        return _inner.Read(buffer);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
