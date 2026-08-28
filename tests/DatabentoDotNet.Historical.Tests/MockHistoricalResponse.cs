using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// One canned answer <see cref="MockHistoricalGateway"/> can be told to give: a body, a status,
/// and whatever misbehaviour the response under test needs.
/// </summary>
/// <remarks>
/// <para>
/// Every shape here is one the historical API actually produces, and each is written from the
/// API's documented behaviour rather than from anything in this library:
/// </para>
/// <list type="bullet">
/// <item><see cref="Json"/> — what <c>metadata.*</c> and <c>symbology.resolve</c> return.</item>
/// <item><see cref="ZstdJsonLines"/> — the zstd-framed JSONL several endpoints return, framed in
/// the <em>body</em> rather than announced in <c>Content-Encoding</c>, which is why a client has
/// to decompress it itself instead of letting <c>HttpClient</c> do it.</item>
/// <item><see cref="Binary"/> — the raw DBN stream <c>timeseries.get_range</c> returns, and the
/// file body a batch download returns. Chunked, and it answers <c>Range: bytes=N-</c>.</item>
/// <item><see cref="SimpleError"/> and <see cref="BusinessError"/> — the two shapes an API error
/// body comes in, which upstream models as <c>ApiErrorResponse::{Simple, Business}</c>
/// (<c>databento-rs/src/historical/client.rs</c>).</item>
/// <item><see cref="Truncated"/> — a complete, well-formed HTTP response whose body stops
/// mid-record.</item>
/// <item><see cref="Dropped"/> — a connection reset partway through the body, which is what
/// upstream's five download retries exist for.</item>
/// </list>
/// <para>
/// <b><see cref="Truncated"/> and <see cref="Dropped"/> are not the same failure</b>, and a client
/// that treats them alike is wrong. A truncated body is a transfer that <em>succeeded</em> — the
/// declared length arrives in full and the stream simply ends short of a whole record, so retrying
/// gets the same bytes again. A dropped connection is a transfer that failed in flight, which is
/// the one worth retrying. The harness keeps them apart so a test can too.
/// </para>
/// <para>
/// <see cref="WithRequestId"/> and <see cref="WithWarnings"/> mutate and return the same instance.
/// Every factory here hands back a fresh one, so chaining reads as a builder without a response
/// ever being shared between two routes by accident.
/// </para>
/// </remarks>
public sealed class MockHistoricalResponse
{
    /// <summary>The content type a JSON body carries.</summary>
    public const string JsonContentType = "application/json";

    /// <summary>The content type a DBN stream, a zstd frame, or a batch file carries.</summary>
    public const string BinaryContentType = "application/octet-stream";

    /// <summary>
    /// The escaping the API's own bodies use, which is the ordinary JSON minimum.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonSerializer"/>'s default encoder is HTML-safe: it renders an apostrophe as
    /// <c>'</c> so a body can be dropped into a <c>&lt;script&gt;</c> block unescaped. Nothing
    /// on this path is HTML, and a harness that escaped where the real server does not would hand
    /// every client below it a body the API never sends — a difference a test would only find by
    /// byte-comparing, which is precisely what the tests here do.
    /// </remarks>
    private static readonly JsonSerializerOptions WireJson =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly List<KeyValuePair<string, string>> _headers = [];

    private MockHistoricalResponse(int statusCode, string contentType, ReadOnlyMemory<byte> body)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
    }

    /// <summary>The HTTP status code, before any <c>206</c> a <c>Range</c> request turns it into.</summary>
    public int StatusCode { get; }

    /// <summary>The <c>Content-Type</c> header value.</summary>
    public string ContentType { get; }

    /// <summary>The body bytes, in full.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>
    /// Whether the body goes out with no <c>Content-Length</c>, and therefore chunked.
    /// </summary>
    public bool Chunked { get; private init; }

    /// <summary>
    /// Whether a <c>Range: bytes=N-</c> request is answered <c>206 Partial Content</c> with the
    /// tail from <c>N</c>, rather than the whole body.
    /// </summary>
    public bool SupportsRange { get; private init; }

    /// <summary>
    /// How many bytes of <see cref="Body"/> to write before resetting the connection, or
    /// <see langword="null"/> to send the whole body and finish cleanly.
    /// </summary>
    public int? DropAfterBytes { get; private init; }

    /// <summary>
    /// What the gateway waits for between writing the prefix and resetting the connection, or
    /// <see langword="null"/> to reset as soon as the prefix has been written.
    /// </summary>
    public Task? DropWhen { get; private init; }

    /// <summary>
    /// Whether <em>every</em> request is answered with <see cref="DropAfterBytes"/> bytes measured
    /// from the requested <c>Range</c> offset, and then a reset.
    /// </summary>
    /// <remarks>
    /// The flaky-link response. See <see cref="DroppedAtAdvancingOffsets"/>; it is a distinct flag
    /// rather than a combination of the others because <see cref="DroppedThenResumable"/> means
    /// the opposite — that a <c>Range</c> request is the one that <em>succeeds</em>.
    /// </remarks>
    public bool DropsEveryRequest { get; private init; }

    /// <summary>The extra response headers — <c>request-id</c>, <c>X-Warning</c>, and any others.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ExtraHeaders => _headers;

    /// <summary>
    /// A JSON body with a <c>Content-Length</c>, as <c>metadata.*</c> and <c>symbology.resolve</c>
    /// return.
    /// </summary>
    /// <param name="json">The body, already serialized.</param>
    /// <param name="statusCode">The status code. Defaults to <c>200</c>.</param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse Json(string json, int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new MockHistoricalResponse(statusCode, JsonContentType, Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// A zstd frame containing newline-delimited JSON, served chunked.
    /// </summary>
    /// <remarks>
    /// The frame is in the body, not in <c>Content-Encoding</c> — that is how the API sends it, and
    /// it is why a client decompresses this itself rather than leaving it to <c>HttpClient</c>'s
    /// automatic decompression, which never sees a <c>Content-Encoding</c> to act on.
    /// </remarks>
    /// <param name="lines">One JSON document per line. A trailing newline is added after each.</param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse ZstdJsonLines(params string[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var jsonl = new StringBuilder();
        foreach (var line in lines)
        {
            jsonl.Append(line).Append('\n');
        }

        using var frame = new MemoryStream();
        using (var encoder = new ZstdSharp.CompressionStream(frame, leaveOpen: true))
        {
            encoder.Write(Encoding.UTF8.GetBytes(jsonl.ToString()));
        }

        return new MockHistoricalResponse(200, BinaryContentType, frame.ToArray()) { Chunked = true };
    }

    /// <summary>
    /// A binary body served chunked, which answers <c>Range: bytes=N-</c> with
    /// <c>206 Partial Content</c> and the tail from <c>N</c>.
    /// </summary>
    /// <remarks>
    /// The shape of both <c>timeseries.get_range</c>, whose body is a raw DBN stream, and a batch
    /// file download, whose body is whatever the job produced. There is no <c>Content-Length</c>,
    /// so the transfer is genuinely chunked — the property that makes back-pressure, a partial
    /// read, and a mid-body drop observable at all.
    /// </remarks>
    /// <param name="body">The body bytes.</param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse Binary(ReadOnlyMemory<byte> body) =>
        new(200, BinaryContentType, body) { Chunked = true, SupportsRange = true };

    /// <summary>
    /// The first <paramref name="length"/> bytes of <paramref name="body"/>, served as a complete
    /// response with a matching <c>Content-Length</c>.
    /// </summary>
    /// <remarks>
    /// The mid-record case. Nothing about this response is malformed at the HTTP layer — the point
    /// is that a client which trusts a successful transfer to contain whole records is wrong, and
    /// the only thing that can tell is the decoder.
    /// </remarks>
    /// <param name="body">The full body.</param>
    /// <param name="length">How much of it to send. Cut inside a record to be worth anything.</param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse Truncated(ReadOnlyMemory<byte> body, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, body.Length);

        return new MockHistoricalResponse(200, BinaryContentType, body[..length]);
    }

    /// <summary>
    /// The first <paramref name="length"/> bytes of <paramref name="body"/>, followed by a
    /// connection reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure upstream's five download retries exist for, and the one an
    /// <c>HttpMessageHandler</c> stub cannot produce at all. There is no <c>Content-Length</c>, so
    /// the transfer is chunked and the connection goes before the terminating chunk — which is what
    /// makes the client read this as a transfer that failed rather than as a body that ended.
    /// </para>
    /// <para>
    /// <b><paramref name="dropWhen"/> is what makes "how much arrived" answerable.</b> Left
    /// <see langword="null"/>, the reset follows the prefix as fast as the handler can issue it,
    /// and whether those last bytes reach the client first is then a property of two TCP stacks
    /// rather than of this harness — a test that byte-compared the prefix would be asserting on a
    /// race. A test that reads the prefix and only then completes <paramref name="dropWhen"/> has
    /// already proved the bytes arrived, and the reset lands after them every time. The gateway
    /// stops waiting after <see cref="MockHistoricalGateway.Timeout"/>, so forgetting to complete
    /// it costs a slow test rather than a hung run.
    /// </para>
    /// <para>
    /// <b><paramref name="statusCode"/> exists so an <em>error</em> body can be dropped too.</b>
    /// A client builds its exception from a failed response's status, its <c>request-id</c> header
    /// and its body — three things read at three different moments — and a transfer that dies
    /// between the second and the third is where a client that does not guard the body read loses
    /// the first two along with it. Without a status here that failure is unrepresentable and the
    /// guard goes untested.
    /// </para>
    /// </remarks>
    /// <param name="body">The full body.</param>
    /// <param name="length">How much of it to write before resetting.</param>
    /// <param name="dropWhen">
    /// What to wait for before resetting, or <see langword="null"/> to reset immediately.
    /// </param>
    /// <param name="statusCode">
    /// The status code the headers carry before the body starts. Defaults to <c>200</c>, which is
    /// the download case this response was written for.
    /// </param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse Dropped(
        ReadOnlyMemory<byte> body,
        int length,
        Task? dropWhen = null,
        int statusCode = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, body.Length);

        return new MockHistoricalResponse(statusCode, BinaryContentType, body)
        {
            Chunked = true,
            DropAfterBytes = length,
            DropWhen = dropWhen,
        };
    }

    /// <summary>
    /// The first <paramref name="length"/> bytes of <paramref name="body"/> followed by a
    /// connection reset — and then, to a request that carries a <c>Range</c>, the tail from where
    /// the reset left off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Dropped"/> and <see cref="Binary"/> combined, which is the shape a resumable
    /// transfer needs and neither of them is on its own. It works because
    /// <c>MockHistoricalGateway</c> answers a satisfiable <c>Range</c> before it looks at
    /// <see cref="DropAfterBytes"/>: the first request carries no <c>Range</c>, so it takes the
    /// drop branch, and the retry carries one, so it takes the <c>206</c> branch and completes.
    /// One registered route, two different answers, decided by the client's own header.
    /// </para>
    /// <para>
    /// <b>This is the response a <em>retry</em> is tested against, not a restart.</b> A client that
    /// retries internally recovers from this within one call. Proving resumption survives a process
    /// ending takes two clients and two gateways — see <c>BatchDownloadTests</c>.
    /// </para>
    /// </remarks>
    /// <param name="body">The full body.</param>
    /// <param name="length">How much of it to write before resetting.</param>
    /// <param name="dropWhen">
    /// What to wait for before resetting, or <see langword="null"/> to reset immediately. See
    /// <see cref="Dropped"/> for why a test that byte-compares the prefix wants this.
    /// </param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse DroppedThenResumable(
        ReadOnlyMemory<byte> body,
        int length,
        Task? dropWhen = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, body.Length);

        return new MockHistoricalResponse(200, BinaryContentType, body)
        {
            Chunked = true,
            SupportsRange = true,
            DropAfterBytes = length,
            DropWhen = dropWhen,
        };
    }

    /// <summary>
    /// A link that dies every time, but a little further along each time: every request is answered
    /// with <paramref name="step"/> bytes from wherever its <c>Range</c> asked to start, and then a
    /// reset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response a retry <em>budget</em> is tested against, as opposed to a single retry.</b>
    /// A client whose retry counter resets on progress finishes this in
    /// <c>ceil(length / step) + 1</c> requests however small <paramref name="step"/> is; a client
    /// whose counter does not reset gives up after its limit, whether or not the transfer was
    /// advancing. Nothing else in this harness tells those two apart, because every other response
    /// either succeeds on the retry or fails identically forever.
    /// </para>
    /// <para>
    /// The last request is the interesting one: it delivers the final bytes and <em>still</em>
    /// resets, so the client sees a failed transfer over a file that is nonetheless complete. What
    /// it does next — ask again, and find nothing left to fetch — is the size-equal case arriving
    /// by a route no other test reaches.
    /// </para>
    /// </remarks>
    /// <param name="body">The full body.</param>
    /// <param name="step">How many bytes to deliver per request before resetting.</param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse DroppedAtAdvancingOffsets(ReadOnlyMemory<byte> body, int step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);

        return new MockHistoricalResponse(200, BinaryContentType, body)
        {
            Chunked = true,
            SupportsRange = true,
            DropAfterBytes = step,
            DropsEveryRequest = true,
        };
    }

    /// <summary>
    /// A binary body served chunked, which answers a <c>Range</c> request with the <b>whole
    /// file</b> and a <c>200</c> rather than with the tail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Binary"/> with range support withheld. A server is entitled to do this — the
    /// header is a request, not a requirement — and it is the case a resuming client gets wrong by
    /// appending the whole file onto the part it already had, producing a longer, corrupt one.
    /// </para>
    /// <para>
    /// Nothing about this response is malformed, which is the point: only a client that compares
    /// the status it got against the request it made can tell this apart from a <c>206</c>.
    /// </para>
    /// </remarks>
    /// <param name="body">The body bytes.</param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse BinaryIgnoringRange(ReadOnlyMemory<byte> body) =>
        new(200, BinaryContentType, body) { Chunked = true };

    /// <summary>
    /// The simple error body: <c>{"detail": "…"}</c>.
    /// </summary>
    /// <param name="statusCode">The status code.</param>
    /// <param name="detail">The message.</param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse SimpleError(int statusCode, string detail) =>
        Json(JsonObject(JsonField("detail", Escape(detail))), statusCode);

    /// <summary>
    /// The business error body: <c>{"detail": {"case": …, "message": …, "docs": …, "payload": …}}</c>.
    /// </summary>
    /// <remarks>
    /// The second of the two shapes upstream deserializes, and the reason its
    /// <c>ApiErrorResponse</c> is an untagged union: <c>detail</c> is a string in one and an object
    /// in the other, and only the JSON tells them apart.
    /// </remarks>
    /// <param name="statusCode">The status code.</param>
    /// <param name="case">The error case, or <see langword="null"/> to omit it.</param>
    /// <param name="message">The message.</param>
    /// <param name="docs">The documentation URL.</param>
    /// <param name="payloadJson">
    /// A JSON object for the <c>payload</c> field, or <see langword="null"/> to omit it. Taken as
    /// JSON rather than as a dictionary so the wire shape stays visible at the call site.
    /// </param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse BusinessError(
        int statusCode,
        string? @case,
        string message,
        string docs,
        string? payloadJson = null)
    {
        var fields = new List<string>(4);
        if (@case is not null)
        {
            fields.Add(JsonField("case", Escape(@case)));
        }

        fields.Add(JsonField("message", Escape(message)));
        fields.Add(JsonField("docs", Escape(docs)));
        if (payloadJson is not null)
        {
            fields.Add(JsonField("payload", payloadJson));
        }

        return Json(
            JsonObject(JsonField("detail", JsonObject([.. fields]))),
            statusCode);
    }

    /// <summary>
    /// Adds the <c>request-id</c> header the API stamps on every response, and which every error
    /// this library reports is required to carry — it is what support asks for first.
    /// </summary>
    /// <param name="requestId">The id.</param>
    /// <returns>This response.</returns>
    public MockHistoricalResponse WithRequestId(string requestId)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        return WithHeader(MockHistoricalGateway.RequestIdHeader, requestId);
    }

    /// <summary>
    /// Adds the <c>X-Warning</c> header, whose value is a JSON array of strings.
    /// </summary>
    /// <remarks>
    /// A JSON array in a header is unusual enough to be worth stating: upstream parses it with
    /// <c>serde_json::from_slice::&lt;Vec&lt;String&gt;&gt;</c> and logs each element, so a client
    /// that treated the header as one opaque string would surface the brackets and quotes to a
    /// user.
    /// </remarks>
    /// <param name="warnings">The warnings.</param>
    /// <returns>This response.</returns>
    public MockHistoricalResponse WithWarnings(params string[] warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        return WithHeader(
            MockHistoricalGateway.WarningHeader,
            JsonSerializer.Serialize(warnings, WireJson));
    }

    /// <summary>
    /// Adds an arbitrary response header — the primitive the two named ones above are built on.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    /// <returns>This response.</returns>
    public MockHistoricalResponse WithHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        _headers.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    // The error bodies are assembled field by field rather than serialized from a type on purpose:
    // their shape is the API's, and writing it out keeps it readable as the wire rather than as a
    // mirror of some class in this project. Escape is still what quotes every string, so a message
    // containing a quote or a newline cannot produce a body that will not parse.
    private static string JsonField(string name, string valueJson) =>
        Escape(name) + ":" + valueJson;

    private static string JsonObject(params string[] fields) =>
        "{" + string.Join(',', fields) + "}";

    private static string Escape(string value) => JsonSerializer.Serialize(value, WireJson);
}
