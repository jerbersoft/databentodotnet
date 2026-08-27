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
/// </remarks>
internal static partial class HistoricalLog
{
    /// <summary>
    /// One warning from the response's <c>X-Warning</c> header. Port of the per-warning
    /// <c>warn!("{warning}")</c> in upstream's <c>check_warnings</c> (<c>client.rs:187</c>).
    /// </summary>
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Warning}")]
    public static partial void ServerWarning(ILogger logger, string warning);

    /// <summary>
    /// The <c>X-Warning</c> header's value did not parse as a JSON array of strings. Port of
    /// upstream's <c>warn!(?err, "Failed to parse server warnings from HTTP header")</c>
    /// (<c>client.rs:243</c>).
    /// </summary>
    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to parse server warnings from the X-Warning header; the request itself is unaffected.")]
    public static partial void MalformedWarningHeader(ILogger logger, Exception exception);

    /// <summary>
    /// An error response's body was neither of the two JSON shapes the API documents. Port of
    /// upstream's <c>warn!("Failed to deserialize error response to expected JSON format: {e:?}")</c>
    /// (<c>client.rs:247</c>).
    /// </summary>
    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "The error response was neither expected JSON shape; using its body as the message verbatim.")]
    public static partial void UnparseableErrorBody(ILogger logger, Exception exception);
}
