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
/// <item><see cref="ZstdJsonLinesFlushedPerLine"/> — the same, compressed the way a server that
/// produces rows as it finds them compresses them: one flushed block per line, so a prefix of the
/// body decodes to a prefix of the rows.</item>
/// <item><see cref="Binary"/> — the raw DBN stream <c>timeseries.get_range</c> returns, and the
/// file body a batch download returns. Chunked, and it answers <c>Range: bytes=N-</c>.</item>
/// <item><see cref="SimpleError"/> and <see cref="BusinessError"/> — the two shapes an API error
/// body comes in, which upstream models as <c>ApiErrorResponse::{Simple, Business}</c>
/// (<c>databento-rs/src/historical/client.rs</c>).</item>
/// <item><see cref="Truncated"/> — a complete, well-formed HTTP response whose body stops
/// mid-record.</item>
/// <item><see cref="Dropped"/> — a connection dropped partway through the body, which is what
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
    /// The <see cref="Body"/> prefix lengths that are complete zstd frames-so-far: after
    /// <c>FlushPoints[i]</c> bytes, every line up to and including line <c>i</c> has been
    /// compressed into finished blocks and decompresses on its own. Empty for a body whose encoder
    /// was never flushed part-way, which is every factory but
    /// <see cref="ZstdJsonLinesFlushedPerLine"/>.
    /// </summary>
    /// <remarks>
    /// What makes a <em>partial</em> zstd body mean anything. A frame written in one shot has no
    /// decodable prefix at all — a client holding the first half of it has nothing it can show
    /// anyone — so a test that serves half a frame and expects a row out of it is testing an
    /// arrangement the API does not produce. These are the offsets at which the arrangement is
    /// real.
    /// </remarks>
    public IReadOnlyList<int> FlushPoints { get; private init; } = [];

    /// <summary>
    /// Whether a <c>Range: bytes=N-</c> request is answered <c>206 Partial Content</c> with the
    /// tail from <c>N</c>, rather than the whole body.
    /// </summary>
    public bool SupportsRange { get; private init; }

    /// <summary>
    /// How many bytes of <see cref="Body"/> to write before dropping the connection, or
    /// <see langword="null"/> to send the whole body and finish cleanly.
    /// </summary>
    public int? DropAfterBytes { get; private init; }

    /// <summary>
    /// What the gateway waits for between writing the prefix and dropping the connection, or
    /// <see langword="null"/> to drop as soon as the prefix has been written.
    /// </summary>
    public Task? DropWhen { get; private init; }

    /// <summary>
    /// Whether <em>every</em> request is answered with <see cref="DropAfterBytes"/> bytes measured
    /// from the requested <c>Range</c> offset, and then a drop.
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
    /// The same zstd-framed JSONL, with the encoder flushed after every line so each one lands in
    /// a block of its own — and <see cref="FlushPoints"/> saying where those blocks end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A server that produces rows as it finds them compresses them as it finds them.</b> A
    /// streaming response has to be decodable while it is still arriving, which means the encoder
    /// flushes rather than holding everything until the frame closes; that is the whole reason
    /// zstd has a flush at all. <see cref="ZstdJsonLines"/> is the opposite extreme — one
    /// <c>Write</c> and one close, so nothing before the end decodes — and a client cannot tell
    /// the two apart from its side, which is exactly why the difference belongs in the harness
    /// rather than in a test's expectations.
    /// </para>
    /// <para>
    /// <b>Flushed per line rather than per some larger batch</b>, because that is the resolution a
    /// test needs: a prefix that decodes to <em>exactly</em> the first row is what tells a reader
    /// that materialises rows lazily apart from one that reads ahead, and any coarser boundary
    /// would let a read-ahead of a few rows pass unnoticed. A real server batches, and a real
    /// server's batch size is not something a test can pin.
    /// </para>
    /// <para>
    /// Nothing about this response drops or stalls on its own. Compose it with
    /// <see cref="Dropped"/> — <c>Dropped(flushed.Body, flushed.FlushPoints[0], dropWhen)</c> —
    /// for a transfer that delivers one decodable row and then holds the connection open, which is
    /// the shape a back-pressure test wants.
    /// </para>
    /// </remarks>
    /// <param name="lines">One JSON document per line. A trailing newline is added after each.</param>
    /// <returns>The response.</returns>
    public static MockHistoricalResponse ZstdJsonLinesFlushedPerLine(params string[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        using var frame = new MemoryStream();
        var flushPoints = new List<int>(lines.Length);
        using (var encoder = new ZstdSharp.CompressionStream(frame, leaveOpen: true))
        {
            foreach (var line in lines)
            {
                encoder.Write(Encoding.UTF8.GetBytes(line + "\n"));

                // Flush, not Write-then-hope: ZstdSharp buffers input until it has a block's worth,
                // so without this the first bytes of the frame would not appear until the encoder
                // was closed. Measured against ZstdSharp.Port 0.8.8 — three 16-byte lines flush at
                // 25, 38 and 52 bytes of a 55-byte frame, and each prefix decompresses to exactly
                // the lines written before it.
                encoder.Flush();
                flushPoints.Add((int)frame.Length);
            }
        }

        return new MockHistoricalResponse(200, BinaryContentType, frame.ToArray())
        {
            Chunked = true,
            FlushPoints = flushPoints,
        };
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
    /// The first <paramref name="length"/> bytes of <paramref name="body"/>, followed by the
    /// connection ending mid-transfer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure upstream's five download retries exist for, and the one an
    /// <c>HttpMessageHandler</c> stub cannot produce at all. There is no <c>Content-Length</c>, so
    /// the transfer is chunked and the connection goes before the terminating chunk — which is what
    /// makes the client read this as a transfer that failed rather than as a body that ended.
    /// </para>
    /// <para>
    /// <b>The prefix always arrives, and that is a change.</b> The gateway half-closes the
    /// connection after writing it, so TCP orders the end behind the bytes; a test may byte-compare
    /// what it received with no signal and no arrangement. It used to reset instead, and a reset
    /// discards whatever the client has not yet read — which on all three CI runners was the entire
    /// response, headers included. See
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/47">#47</see>.
    /// </para>
    /// <para>
    /// <b><paramref name="dropWhen"/> is therefore about <em>when</em>, not about <em>whether</em>.</b>
    /// Left <see langword="null"/>, the connection ends as soon as the prefix is on the wire. A test
    /// that needs the transfer held open instead — to cancel it part-way, to end a process around
    /// it — passes a task and completes it when it is ready. The gateway stops waiting after
    /// <see cref="MockHistoricalGateway.Timeout"/>, so forgetting to complete it costs a slow test
    /// rather than a hung run.
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
    /// <param name="length">How much of it to write before dropping.</param>
    /// <param name="dropWhen">
    /// What to wait for before dropping, or <see langword="null"/> to drop immediately.
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
    /// The first <paramref name="length"/> bytes of <paramref name="body"/> followed by the
    /// connection ending — and then, to a request that carries a <c>Range</c>, the tail from where
    /// it left off.
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
    /// <param name="length">How much of it to write before dropping.</param>
    /// <param name="dropWhen">
    /// What to wait for before dropping, or <see langword="null"/> to drop immediately. See
    /// <see cref="Dropped"/> for what a test still wants this for.
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
    /// with <paramref name="step"/> bytes from wherever its <c>Range</c> asked to start, and then
    /// the connection ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The response a retry <em>budget</em> is tested against, as opposed to a single retry.</b>
    /// A client whose retry counter resets on progress finishes this in
    /// <c>ceil(length / step)</c> requests however small <paramref name="step"/> is; a client whose
    /// counter does not reset gives up after its limit, whether or not the transfer was advancing.
    /// Nothing else in this harness tells those two apart, because every other response either
    /// succeeds on the retry or fails identically forever.
    /// </para>
    /// <para>
    /// <b>That count is exact, and it is the half-close that makes it so.</b> Every answer delivers
    /// its whole step, so the number of requests is arithmetic rather than a race — which it was
    /// not while this reset the connection instead, and which is how
    /// <see href="https://github.com/jerbersoft/databentodotnet/issues/47">#47</see> was found: a
    /// reset that delivered nothing left the file never advancing and the budget deciding.
    /// </para>
    /// <para>
    /// The last request is the interesting one: it delivers the final bytes and the connection
    /// <em>still</em> dies, so the client sees a failed transfer over a file that is nonetheless
    /// complete. What it does next — look again, and find nothing left to fetch — is the size-equal
    /// case arriving by a route no other test reaches.
    /// </para>
    /// </remarks>
    /// <param name="body">The full body.</param>
    /// <param name="step">How many bytes to deliver per request before dropping.</param>
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
