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
/// an incomplete port — it is this rule applied. Every message here sits at a point where the
/// exception is <em>swallowed</em>: a malformed <c>X-Warning</c> header is logged because the
/// request deliberately carries on without it, and an unparseable error body is logged because the
/// exception describing it is replaced by a <see cref="DatabentoApiException"/> carrying the body
/// verbatim. In both, the log line is the only surviving record.
/// </para>
/// <para>
/// <b>Upstream's two JSON-decode logs are therefore deliberately not ported</b>, and the omission
/// is an improvement rather than a gap. <c>deserialize_json</c> (<c>client.rs:231-237</c>) and the
/// per-line <c>error!</c> in <c>handle_zstd_jsonl_response</c> (<c>client.rs:224</c>) sit where
/// this port <em>throws</em>: a <see cref="System.Text.Json.JsonException"/> reaches the caller
/// carrying its <c>Path</c>, <c>LineNumber</c> and <c>BytePositionInLine</c> — more than upstream's
/// flattened <c>crate::Error::from(err)</c> preserves — so a log line would duplicate what the
/// caller already holds. And upstream's interpolates <c>?str</c>, which for <c>handle_response</c>
/// is the <em>entire response body</em>: unbounded in size, and market data belonging to the
/// caller's customers written into their logs at <c>error</c> level by a library they did not
/// configure for it. A reader arriving from an unlogged <c>JsonException</c> is looking at a
/// decision, not an oversight.
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
}
