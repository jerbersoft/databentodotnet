using DatabentoDotNet.Dbn;
using DatabentoDotNet.Dbn.Internal;

namespace DatabentoDotNet.Historical;

/// <summary>
/// The <c>timeseries.*</c> endpoints: the market data itself.
/// </summary>
/// <remarks>
/// <para>
/// Reached through <see cref="HistoricalClient.Timeseries"/> rather than constructed. Port of
/// upstream's <c>TimeseriesClient</c> (<c>timeseries.rs:19-22</c>).
/// </para>
/// <para>
/// <b>Everything on this facade costs money, and nothing else in this library does.</b> Every
/// <c>metadata.*</c> and <c>symbology.*</c> endpoint is discovery or a billing enquiry, answered
/// free. These two move data and are billed for the bytes they move. Call
/// <see cref="MetadataClient.GetCostAsync"/> first — <see cref="GetRangeParams.ToQuery"/> exists so
/// that pricing a request and sending it cannot drift apart, which is a thing upstream leaves to
/// the caller to get right by hand.
/// </para>
///
/// <code>
/// var request = new GetRangeParams { /* … */ };
///
/// var dollars = await client.Metadata.GetCostAsync(request.ToQuery(), ct);
/// if (dollars &lt;= budget)
/// {
///     await using var data = await client.Timeseries.GetRangeAsync(request, ct);
/// }
/// </code>
/// </remarks>
public sealed class TimeseriesClient
{
    /// <summary>
    /// The <c>encoding</c> every request sends. Not a parameter: this client returns a decoder, so
    /// DBN is the only encoding it could ask for.
    /// </summary>
    public const string RequestEncoding = "dbn";

    /// <summary>
    /// The <c>compression</c> every request sends. Not a parameter either — it is what makes a
    /// multi-gigabyte range a reasonable thing to ask for, and upstream hard-codes it identically
    /// (<c>timeseries.rs:131-134</c>).
    /// </summary>
    public const string RequestCompression = "zstd";

    /// <summary>
    /// The <c>Accept</c> these requests carry. The only non-JSON request in the historical API —
    /// upstream marks it with that same observation in a comment (<c>timeseries.rs:141</c>).
    /// </summary>
    /// <remarks>
    /// The server answers <c>Content-Type: application/zstd</c> regardless, which is not a
    /// contradiction: this header says what the client will accept, not what it expects to be
    /// called.
    /// </remarks>
    public const string BinaryMediaType = "application/octet-stream";

    private readonly HistoricalClient _client;

    internal TimeseriesClient(HistoricalClient client) => _client = client;

    /// <summary>
    /// Downloads a range of records and returns a reader over them. <b>This costs money.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port of upstream's <c>get_range</c> (<c>timeseries.rs:88-97</c>) and its private
    /// <c>get_range_impl</c>. <b>Price it first</b> with
    /// <see cref="MetadataClient.GetCostAsync"/>, passing
    /// <see cref="GetRangeParams.ToQuery"/> — that way the request you priced is the request you
    /// send.
    /// </para>
    /// <para>
    /// <b>An empty result is a stream, not an error.</b> A range the dataset has no records for
    /// answers <c>200</c> with a well-formed metadata block, no records, and
    /// <c>X-Warning: No data found for the request you submitted.</c> — which
    /// <see cref="HistoricalClient"/> logs. The returned stream yields nothing and throws nothing.
    /// </para>
    /// <para>
    /// <b>The response is chunked, with no <c>Content-Length</c>.</b> Its size is not known until
    /// it ends, which is why nothing here pre-sizes a buffer and why a connection dropped mid-body
    /// surfaces from <see cref="TimeseriesReader.FillBufferAsync"/> as an
    /// <see cref="IOException"/> rather than as a clean end.
    /// </para>
    /// </remarks>
    /// <param name="parameters">What to download, over what range.</param>
    /// <param name="cancellationToken">Cancels the request and the download.</param>
    /// <returns>A reader over the records, positioned at the first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="DatabentoApiException">
    /// The API refused the request — an empty range is <c>422 data_time_range_start_on_or_after_end</c>,
    /// though <see cref="DateTimeRange"/> refuses to build one in the first place.
    /// </exception>
    /// <exception cref="DbnDecodeException">The response is not a valid DBN stream.</exception>
    /// <example>
    /// <code>
    /// // Price it first — metadata.get_cost is free and takes the same parameters through ToQuery().
    /// decimal cost = await client.Metadata.GetCostAsync(request.ToQuery());
    ///
    /// await using var reader = await client.Timeseries.GetRangeAsync(request);
    ///
    /// await foreach (OwnedRecord record in reader.ReadRecordsAsync())
    /// {
    ///     if (record.TryGet(out TradeMsg trade))
    ///     {
    ///         Console.WriteLine($"{DbnTime.ToInstant(trade.IndexTs)} {trade.Price} x {trade.Size}");
    ///     }
    /// }
    /// </code>
    /// </example>
    public async Task<TimeseriesReader> GetRangeAsync(
        GetRangeParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var response = await _client
            .SendAsync(HttpMethod.Post, Slug("get_range"), parameters.ToFormParameters(), BinaryMediaType, cancellationToken)
            .ConfigureAwait(false);

        // From here the response owns the socket and the stream chain owns the response, so every
        // failure below has to tear down what it built — the caller has no handle on any of it
        // until this method returns.
        Stream? body = null;
        Stream? frame = null;
        try
        {
            body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            frame = ZstdDecompressor.Decompress(body);

            var stream = await TimeseriesReader
                .OpenAsync(frame, _client.UpgradePolicy, leaveOpen: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // OpenAsync owns `frame` now, and `frame` owns `body`. Only the response is still
            // ours, and it must outlive neither — disposing it here would close the socket the
            // caller is about to read from, so it is handed to the reader to dispose with them.
            return stream.OwningResponse(response);
        }
        catch
        {
            // OpenAsync disposes `frame` itself on failure, and `frame` disposes `body` with it —
            // so only the untaken ends of the chain are cleaned up here.
            if (frame is null)
            {
                body?.Dispose();
            }

            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Downloads a range of records to a file, then returns a reader over that file.
    /// <b>This costs money.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This does not port the way upstream writes it, and the difference is visible to
    /// callers.</b> Upstream decodes the response and <em>re-encodes</em> it to disk with
    /// <c>AsyncDbnEncoder</c> (<c>timeseries.rs:100-115</c>), applying the upgrade policy on the
    /// way through. This library has no record encoder and deliberately will not have one
    /// (CLAUDE.md, "Testing"), so that route is closed — and it is also unnecessary. The response
    /// body already <em>is</em> zstd-framed DBN: writing it to disk is a byte copy, with no decode,
    /// no re-encode, and nothing to get wrong in between. The file that lands is bit-identical to
    /// what the API served.
    /// </para>
    /// <para>
    /// <b>The consequence, stated rather than left to be discovered.</b> Upstream's file holds
    /// records at the <em>upgraded</em> version and is read back with <c>AsIs</c>; this one holds
    /// them at the version the API sent, and <see cref="HistoricalClient.UpgradePolicy"/> applies
    /// each time the file is read. A file written by this library and read by upstream's — or by a
    /// later version of this one — gets that reader's upgrade behaviour rather than this writer's.
    /// Ours is the more defensible of the two, since a cached response that is not what the server
    /// sent is a cache that can lie, but it is a difference and not a detail.
    /// </para>
    /// <para>
    /// <b>The server names the file too, and it is ignored.</b> The response carries
    /// <c>Content-Disposition: attachment; filename=…</c>. <paramref name="path"/> wins, as it does
    /// upstream; the header is unused deliberately rather than unnoticed, because a caller who
    /// named a path did not ask to be second-guessed by a remote host.
    /// </para>
    /// </remarks>
    /// <param name="parameters">What to download, over what range.</param>
    /// <param name="path">
    /// Where to write the raw <c>.dbn.zst</c> body. Overwritten if it exists; created along with
    /// any missing parent directory.
    /// </param>
    /// <param name="cancellationToken">Cancels the request, the download and the write.</param>
    /// <returns>A reader over the file just written, positioned at the first record.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="DatabentoApiException">The API refused the request.</exception>
    /// <exception cref="IOException">The download or the write failed.</exception>
    public async Task<TimeseriesReader> GetRangeToFileAsync(
        GetRangeParams parameters,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        using (var response = await _client
            .SendAsync(HttpMethod.Post, Slug("get_range"), parameters.ToFormParameters(), BinaryMediaType, cancellationToken)
            .ConfigureAwait(false))
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                var file = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0, useAsync: true);

                await using (file.ConfigureAwait(false))
                {
                    await body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return await OpenFileAsync(path, _client.UpgradePolicy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a <c>.dbn.zst</c> file written by <see cref="GetRangeToFileAsync"/>.
    /// </summary>
    /// <remarks>
    /// Separate from the download so a cached file can be re-read without one, which is most of the
    /// reason to write it to disk in the first place.
    /// </remarks>
    /// <param name="path">The file to read.</param>
    /// <param name="upgradePolicy">How to present records from an older DBN version.</param>
    /// <param name="cancellationToken">Cancels the metadata read.</param>
    /// <returns>A reader over the file, positioned at the first record.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="DbnDecodeException">The file is not a valid DBN stream.</exception>
    public static async Task<TimeseriesReader> OpenFileAsync(
        string path,
        VersionUpgradePolicy upgradePolicy = VersionUpgradePolicy.UpgradeToV3,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, useAsync: true);

        Stream? frame = null;
        try
        {
            frame = ZstdDecompressor.Decompress(file);
            return await TimeseriesReader
                .OpenAsync(frame, upgradePolicy, leaveOpen: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (frame is null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// The endpoint group's slug prefix, built the way upstream builds it
    /// (<c>timeseries.rs:159-161</c>) before the transport prepends the API version.
    /// </summary>
    private static string Slug(string endpoint) => $"timeseries.{endpoint}";
}
