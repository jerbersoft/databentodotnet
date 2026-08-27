using System.Net;
using System.Text.Json;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The Databento API rejected a request: the HTTP response carried a non-success status code.
/// </summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>ApiError</c> (<c>error.rs:63-79</c>) and its <c>Display</c>
/// (<c>error.rs:104-125</c>). Upstream wraps this as one arm, <c>Error::Api</c>, of a single enum
/// spanning live, historical, and reference errors; PORTING.md §2 splits that enum by module
/// instead, so a caller of the historical client is never in a position to catch a live-gateway
/// exception it could not possibly have raised. This type is named
/// <c>DatabentoApiException</c> rather than <c>ApiException</c> for the same reason
/// <c>DatabentoAuthenticationException</c> in <c>DatabentoDotNet.Live</c> is — the type name
/// itself is exempt from the repo's <c>DatabentoDotNet.*</c>-not-<c>Databento.*</c> naming rule,
/// which is about packages, assemblies, and namespaces (CLAUDE.md, "Naming").
/// </para>
/// </remarks>
public sealed class DatabentoApiException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public DatabentoApiException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public DatabentoApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabentoApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates the exception from the parts of an API error response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Exception.Message"/> is composed the way upstream's <c>Display</c> composes it
    /// (<c>error.rs:104-125</c>), in the same order and from the same parts — nothing this
    /// library owns is interpolated into it, only what the response itself carried:
    /// <c>"{requestId} failed with {statusCode} {message}{docs}{case}"</c> when there is a
    /// request id, and <c>"{statusCode} {message}{docs}{case}"</c> when there is not, where
    /// <c>docs</c> is <c>" See {docsUrl} for documentation."</c> or empty, and <c>case</c> is
    /// <c>" (case: {errorCase})"</c> or empty.
    /// </para>
    /// <para>
    /// <b>The <c>statusCode</c> segment renders differently from upstream's, and that is a
    /// documented departure, not an oversight.</b> Upstream's <c>reqwest::StatusCode</c> has a
    /// canonical-reason-phrase table and its <c>Display</c> uses it — <c>400 Bad Request</c>.
    /// <see cref="System.Net.HttpStatusCode"/> carries no such table; its default
    /// <c>ToString()</c> is the enum member's own PascalCase name with no separating space and no
    /// leading number (<c>BadRequest</c>). Pulling in a reason-phrase table from elsewhere (for
    /// example ASP.NET Core's <c>ReasonPhrases</c>) to reproduce the exact upstream text would add
    /// a dependency to a shipping HTTP *client* library for the sake of one string, so this keeps
    /// the BCL's own rendering. The numeric code is never lost — it is <see cref="StatusCode"/>,
    /// unconditionally.
    /// </para>
    /// </remarks>
    /// <param name="statusCode">The HTTP status code of the response.</param>
    /// <param name="requestId">
    /// The <c>request-id</c> response header, or <see langword="null"/> when the response carried
    /// none.
    /// </param>
    /// <param name="errorCase">
    /// A machine-readable identifier for the error case, when the server returns a structured
    /// error envelope, or <see langword="null"/> for an unstructured error.
    /// </param>
    /// <param name="message">The message from the Databento API.</param>
    /// <param name="docsUrl">
    /// The link to documentation related to the error, or <see langword="null"/> when the server
    /// provides none.
    /// </param>
    /// <param name="payload">
    /// Additional context for the error, when the server provides one — common keys include
    /// <c>dataset</c>, <c>start</c>, <c>end</c>, <c>available_start</c>, and
    /// <c>available_end</c> — or <see langword="null"/> when it does not. Each element is cloned;
    /// see <see cref="Payload"/>.
    /// </param>
    public DatabentoApiException(
        HttpStatusCode statusCode,
        string? requestId,
        string? errorCase,
        string message,
        string? docsUrl,
        IReadOnlyDictionary<string, JsonElement>? payload)
        : base(ComposeMessage(statusCode, requestId, errorCase, message, docsUrl))
    {
        StatusCode = statusCode;
        RequestId = requestId;
        Case = errorCase;
        DocsUrl = docsUrl;
        Payload = ClonePayload(payload);
    }

    /// <summary>The HTTP status code of the response.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// The <c>request-id</c> response header, or <see langword="null"/> when the response carried
    /// none.
    /// </summary>
    /// <remarks>
    /// This is what support asks for first when a request needs investigating (ROADMAP.md §5) —
    /// surfacing it as its own property, rather than leaving it buried in <see cref="Exception.Message"/>,
    /// is what lets a caller log or report it without parsing prose.
    /// </remarks>
    public string? RequestId { get; }

    /// <summary>
    /// A machine-readable identifier for the error case, when the server returns a structured
    /// error envelope, or <see langword="null"/> for an unstructured error.
    /// </summary>
    public string? Case { get; }

    /// <summary>
    /// The link to documentation related to the error, or <see langword="null"/> when the server
    /// provides none.
    /// </summary>
    public string? DocsUrl { get; }

    /// <summary>
    /// Additional context for the error, when the server provides one — common keys include
    /// <c>dataset</c>, <c>start</c>, <c>end</c>, <c>available_start</c>, and
    /// <c>available_end</c>. <see langword="null"/> when the server sent none; never an empty
    /// dictionary standing in for absent, because upstream's <c>Option&lt;Box&lt;HashMap&lt;..&gt;&gt;&gt;</c>
    /// distinguishes the two and this port does too.
    /// </summary>
    /// <remarks>
    /// Every element is <see cref="JsonElement.Clone"/>d at construction, deliberately. An
    /// un-cloned <see cref="JsonElement"/> points into the buffer owned by the
    /// <see cref="JsonDocument"/> that produced it, and that document is disposed as soon as the
    /// response finishes being read — so a property that held the elements as handed in would
    /// hand the caller a use-after-dispose the moment they read it after the fact, not a compile
    /// error the type system could ever catch. Cloning here makes the dictionary own its own
    /// memory and outlive the document that produced it.
    /// </remarks>
    public IReadOnlyDictionary<string, JsonElement>? Payload { get; }

    private static string ComposeMessage(
        HttpStatusCode statusCode,
        string? requestId,
        string? errorCase,
        string message,
        string? docsUrl)
    {
        var docs = docsUrl is null ? string.Empty : $" See {docsUrl} for documentation.";
        var @case = errorCase is null ? string.Empty : $" (case: {errorCase})";

        return requestId is null
            ? $"{statusCode} {message}{docs}{@case}"
            : $"{requestId} failed with {statusCode} {message}{docs}{@case}";
    }

    private static Dictionary<string, JsonElement>? ClonePayload(
        IReadOnlyDictionary<string, JsonElement>? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var clone = new Dictionary<string, JsonElement>(payload.Count);
        foreach (var (key, value) in payload)
        {
            clone[key] = value.Clone();
        }

        return clone;
    }
}
