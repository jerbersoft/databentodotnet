using Microsoft.Extensions.Logging;

namespace DatabentoDotNet.Historical.Internal;

/// <summary>
/// Source-generated log messages for the historical client.
/// </summary>
/// <remarks>
/// <para>
/// Per PORTING.md §2 ("<c>tracing</c> → <c>ILogger</c> with source-generated messages") and D2:
/// upstream logs through <c>tracing::warn!</c> at the three call sites named below, and this is
/// where a caller who configures <c>HistoricalClient.LoggerFactory</c> sees the same information.
/// Without a factory configured, these resolve to <c>NullLogger.Instance</c> — no logging
/// configured, no logging done, and no cost paid for a caller who never asks for it.
/// </para>
/// <para>
/// <b>Event ids are stable identifiers and are not to be renumbered.</b> A caller can filter or
/// react to a specific message by id; changing one out from under them silently breaks that, in a
/// way no compiler catches. Add a new id for a new message rather than reusing or shifting one of
/// these.
/// </para>
/// <para>
/// <b>The rule for what belongs here: this library logs only what the caller cannot otherwise
/// see.</b> Upstream has more <c>tracing</c> sites than the three below, and the difference is not
/// an incomplete port — it is this rule applied. Two of the three sit where an exception is
/// <em>swallowed</em>: a malformed <c>X-Warning</c> header is logged because the request
/// deliberately carries on without it, and an unparseable error body is logged because the
/// exception describing it is replaced by a <see cref="DatabentoApiException"/> carrying the body
/// verbatim. In both, the log line is the only surviving record.
/// </para>
/// <para>
/// <b><see cref="ServerWarning"/> is not one of those, and the governing rule is what admits
/// it.</b> No exception is involved on its path at all — it is called with a parsed string on the
/// success side of that <c>catch</c>. It qualifies because a caller who reaches the API through
/// <c>SendJsonAsync</c> never holds the <see cref="System.Net.Http.HttpResponseMessage"/> and so
/// can never read the header for themselves; the log is their only route to it. Do not narrow the
/// governing rule to the swallowed-exception case — doing so would rule out the one message
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/35">#35</see>'s definition of
/// done is actually about.
/// </para>
/// <para>
/// <b>Two further upstream sites are therefore deliberately not ported</b>, and the omission is an
/// improvement rather than a gap. <c>deserialize_json</c> (<c>client.rs:231-236</c>) logs a JSON
/// decode failure; the per-line <c>error!</c> in <c>handle_zstd_jsonl_response</c>
/// (<c>client.rs:224</c>) is <em>not</em> a JSON log despite sitting on that path — it fires when
/// <c>next_line()</c> fails, which is a zstd or IO failure, the JSON decode there being
/// <c>deserialize_json</c> at <c>:226</c>. Both sit where this port <em>throws</em> instead, which
/// is why neither ports: what fails reaches the caller as the exception it was, so a log line would
/// only duplicate what they already hold.
/// </para>
/// <para>
/// <b>Which exception, exactly — because guessing this wrong is how the filter on
/// <c>CreateApiExceptionAsync</c>'s catch nearly ended up naming one type.</b> A JSON decode
/// failure arrives as a <see cref="System.Text.Json.JsonException"/> carrying <c>Path</c>,
/// <c>LineNumber</c> and <c>BytePositionInLine</c>; upstream's <c>crate::Error::from(err)</c> keeps
/// the <c>serde_json::Error</c> whole through its <c>#[from]</c>, so it carries the equivalent
/// <c>line</c> and <c>column</c> and the .NET one adds <c>Path</c> on top. A failed <em>read</em>
/// arrives as an <see cref="System.IO.IOException"/> <em>or</em> as the
/// <see cref="System.Net.Http.HttpRequestException"/> that wraps one — see the measured note at
/// that catch rather than trusting this sentence. A failed <em>decompression</em> is neither: a
/// corrupt frame throws <c>ZstdSharp.ZstdException</c>, which derives straight from
/// <see cref="System.Exception"/>, and a frame that merely ends early throws
/// <see cref="System.IO.EndOfStreamException"/>, which is an
/// <see cref="System.IO.IOException"/>. Measured, all three.
/// </para>
/// <para>
/// And <c>deserialize_json</c> interpolates <c>?str</c>, which for
/// <c>handle_response</c> is the <em>entire response body</em>: unbounded in size, and market data
/// belonging to the caller's customers written into their logs at <c>error</c> level by a library
/// they did not configure for it. A reader arriving from an unlogged exception on either path is
/// looking at a decision, not an oversight.
/// </para>
/// </remarks>
internal static partial class HistoricalLog
{
    /// <summary>
    /// One warning from the response's <c>X-Warning</c> header. Port of the per-warning
    /// <c>warn!("{warning}")</c> in upstream's <c>check_warnings</c> (<c>client.rs:243</c>).
    /// </summary>
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Warning}")]
    public static partial void ServerWarning(ILogger logger, string warning);

    /// <summary>
    /// The <c>X-Warning</c> header's value did not parse as a JSON array of strings. Port of
    /// upstream's <c>warn!(?err, "Failed to parse server warnings from HTTP header")</c>
    /// (<c>client.rs:247</c>).
    /// </summary>
    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to parse server warnings from the X-Warning header; the request itself is unaffected.")]
    public static partial void MalformedWarningHeader(ILogger logger, Exception exception);

    /// <summary>
    /// An error response's body was neither of the two JSON shapes the API documents. Port of
    /// upstream's <c>warn!("Failed to deserialize error response to expected JSON format: {e:?}")</c>
    /// (<c>client.rs:187</c>).
    /// </summary>
    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "The error response was neither expected JSON shape; using its body as the message verbatim.")]
    public static partial void UnparseableErrorBody(ILogger logger, Exception exception);

    /// <summary>
    /// A batch file's checksum names an algorithm this library cannot compute, so the downloaded
    /// bytes were not verified. Port of upstream's
    /// <c>warn!(hash_algo, "Skipping checksum with unsupported hash algorithm")</c>
    /// (<c>batch.rs:261-264</c>).
    /// </summary>
    /// <remarks>
    /// <b>The governing rule admits this one twice over.</b> The download succeeds and returns a
    /// path, so nothing in the return value says the file was taken on trust — and it is the exact
    /// case #39's own porting notes single out, because this library otherwise <em>throws</em> on a
    /// mismatch and this is the one path where that guarantee quietly does not apply.
    /// </remarks>
    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "The batch file '{Filename}' advertises a '{Algorithm}' checksum, which this library "
            + "cannot compute, so its contents were not verified.")]
    public static partial void ChecksumSkipped(ILogger logger, string filename, string algorithm);

    /// <summary>
    /// A batch file's transfer failed part-way and is being retried from where it stopped. Port of
    /// upstream's <c>error!(?err, retries, "Retrying download")</c> (<c>batch.rs:301</c>), at
    /// warning level rather than error because the transfer has not failed yet.
    /// </summary>
    /// <remarks>
    /// The exception is swallowed by the retry, so this log line is its only surviving record —
    /// the first half of the governing rule, exactly as for
    /// <see cref="MalformedWarningHeader"/>.
    /// </remarks>
    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "The transfer of batch file '{Filename}' failed after {BytesOnDisk} byte(s); retrying "
            + "({Retries} of {MaximumRetries}).")]
    public static partial void DownloadRetry(
        ILogger logger,
        Exception exception,
        string filename,
        long bytesOnDisk,
        int retries,
        int maximumRetries);

    /// <summary>
    /// A resumed transfer asked for a byte range and the server answered with the whole file, so
    /// the partial file was discarded and fetched again from the beginning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a port — upstream has no equivalent, and no check either.</b> Upstream opens the
    /// output file in append mode before it looks at the status, so a <c>200</c> answering a
    /// <c>Range</c> request appends a whole second copy of the file onto the partial one. That
    /// produces a file larger than expected whose checksum fails, which upstream reports as a
    /// warning and returns success for.
    /// </para>
    /// <para>
    /// A server answering <c>200</c> to a <c>Range</c> request is doing nothing wrong — the header
    /// is a request, not a requirement — so this is recovered from rather than thrown on. It is
    /// logged because it means resumption is silently not working, which is the one guarantee
    /// #39 exists to provide.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "The server answered the resumed transfer of '{Filename}' with the whole file rather "
            + "than the {BytesOnDisk} byte(s) still outstanding, so it is being fetched from the start.")]
    public static partial void ResumeNotHonoured(ILogger logger, string filename, long bytesOnDisk);
}
