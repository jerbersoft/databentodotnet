using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DatabentoDotNet.Historical.Tests;

/// <summary>
/// The bodies and paths the <c>batch.*</c> tests serve, and the checksums that go with them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The JSON here is Databento's, transcribed from a live response rather than composed.</b>
/// #39 probed <c>batch.list_jobs</c>, <c>batch.get_job_details</c> and <c>batch.list_files</c>
/// against <c>hist.databento.com</c> and this is what came back, field for field and spelling for
/// spelling — including the two fields upstream's <c>BatchJob</c> does not model, the <c>null</c>s
/// where a "none" would be expected, and the <c>ftp</c> URL beside the <c>https</c> one. A body
/// this harness invented would agree with whatever the client believed.
/// </para>
/// <para>
/// <b>The download URLs name a host the gateway is not.</b> That is deliberate and it is what the
/// real API does: the file URLs point at <c>api.databento.com</c> while the API answers at
/// <c>hist.databento.com</c>. A client that followed the URL as given would leave the loopback
/// gateway entirely — and take the API key to a host the test never configured — so every download
/// test here doubles as a check that only the path is used.
/// </para>
/// </remarks>
public static class BatchFixture
{
    /// <summary>The job id every fixture body uses, in the API's own format.</summary>
    public const string JobId = "XNAS-20260825-6T3F5G5TYH";

    /// <summary>The user id the download paths are namespaced by.</summary>
    public const string UserId = "W7KFYTCU";

    /// <summary>The host the API names in a file's <c>https</c> URL, which is not the API's own.</summary>
    public const string DownloadHost = "https://api.databento.com";

    /// <summary>The name of the data file in <see cref="FileListJson"/>.</summary>
    public const string DataFilename = "xnas-itch-20220610.ohlcv-1m.csv";

    /// <summary>The name of the condition file in <see cref="FileListJson"/>.</summary>
    public const string ConditionFilename = "condition.json";

    /// <summary>
    /// One job as <c>batch.get_job_details</c> returned it, with every field the API sends.
    /// </summary>
    /// <remarks>
    /// Note what is <c>null</c> and would not be if this had been written from upstream's struct:
    /// <c>compression</c> and <c>split_duration</c> both spell their "none" as JSON <c>null</c>,
    /// and <c>bill_id</c> and <c>packaging</c> are fields upstream drops on the floor.
    /// </remarks>
    public const string JobJson =
        """
        {"id":"XNAS-20260825-6T3F5G5TYH","user_id":"W7KFYTCU","bill_id":null,"cost_usd":0.0,
        "dataset":"XNAS.ITCH","symbols":"MSFT","stype_in":"raw_symbol","stype_out":"instrument_id",
        "schema":"ohlcv-1m","start":"2022-06-10T12:30:00.000000000Z","end":"2022-06-10T14:00:00.000000000Z",
        "limit":1000,"encoding":"csv","compression":null,"pretty_px":false,"pretty_ts":false,
        "map_symbols":true,"split_symbols":false,"split_duration":null,"split_size":null,
        "packaging":null,"delivery":"download","record_count":90,"billed_size":5040,
        "actual_size":5040,"package_size":10578,"state":"done",
        "ts_received":"2026-08-25T18:58:13.023009000Z","ts_queued":"2026-08-25T18:58:33.044278000Z",
        "ts_process_start":"2026-08-25T18:58:43.081175000Z",
        "ts_process_done":"2026-08-25T18:58:44.096437000Z",
        "ts_expiration":"2026-09-24T19:00:00.000000000Z","progress":100}
        """;

    /// <summary>The short form <c>batch.list_jobs</c> returns, for four jobs.</summary>
    public const string JobSummaryListJson =
        """
        [{"id":"XNAS-20260825-WEF7BHTY4S","state":"done","ts_received":"2026-08-25T18:58:13.015707000Z"},
         {"id":"XNAS-20260825-6T3F5G5TYH","state":"done","ts_received":"2026-08-25T18:58:13.023009000Z"},
         {"id":"XNAS-20260825-MG4FSJ4Q8L","state":"done","ts_received":"2026-08-25T18:58:13.033056000Z"},
         {"id":"XNAS-20260828-MBMBD89WX7","state":"done","ts_received":"2026-08-28T15:48:06.998666000Z"}]
        """;

    /// <summary>Two of the four files <c>batch.list_files</c> returned for <see cref="JobId"/>.</summary>
    /// <remarks>
    /// Cut to two so a test can assert on an exact list; the hashes and sizes are the real ones,
    /// which is what makes <see cref="ConditionFilename"/> usable as a body whose checksum a test
    /// did not compute for itself.
    /// </remarks>
    public const string FileListJson =
        """
        [{"filename":"condition.json","size":122,
          "hash":"sha256:ce5db37329231c02e6b3535878aa9bb57136d9ebacc1e9fa8db611f5b1e08531",
          "urls":{"https":"https://api.databento.com/v0/batch/download/W7KFYTCU/XNAS-20260825-6T3F5G5TYH/condition.json",
                  "ftp":"ftp://ftp.databento.com/W7KFYTCU/XNAS-20260825-6T3F5G5TYH/condition.json"}},
         {"filename":"xnas-itch-20220610.ohlcv-1m.csv","size":8341,
          "hash":"sha256:d1e564302b6376051c7083a61a1a653f018cb3b6c3197c1dc889f9e7e14ce912",
          "urls":{"https":"https://api.databento.com/v0/batch/download/W7KFYTCU/XNAS-20260825-6T3F5G5TYH/xnas-itch-20220610.ohlcv-1m.csv",
                  "ftp":"ftp://ftp.databento.com/W7KFYTCU/XNAS-20260825-6T3F5G5TYH/xnas-itch-20220610.ohlcv-1m.csv"}}]
        """;

    /// <summary>The slug a batch file is served at — the path of its URL, less the version prefix.</summary>
    /// <remarks>
    /// <c>MockHistoricalGateway.PathFor</c> prepends <c>/v0/</c>, and the API's own download URLs
    /// are <c>/v0/batch/download/{user}/{job}/{file}</c>, so a file registers as a slug exactly
    /// the way an endpoint does. Both <c>HistoricalClient.PathFor</c> and
    /// <c>MockHistoricalGateway.Get</c> say so in their remarks; this is the method that relies on
    /// it.
    /// </remarks>
    /// <param name="filename">The file's name.</param>
    /// <returns>The slug to register the file's route under.</returns>
    public static string DownloadSlug(string filename) =>
        $"batch/download/{UserId}/{JobId}/{filename}";

    /// <summary>
    /// A <c>batch.list_files</c> body naming one file, with a hash this method computes over
    /// <paramref name="body"/>.
    /// </summary>
    /// <remarks>
    /// The hash is computed here rather than asserted against, because these tests are about what
    /// the client does when the hash matches or does not — not about SHA-256. Pass
    /// <paramref name="advertisedHash"/> to publish a hash that is deliberately wrong, which is
    /// the corrupted-body case.
    /// </remarks>
    /// <param name="filename">The file's name.</param>
    /// <param name="body">The bytes the file's route will serve.</param>
    /// <param name="advertisedHash">
    /// The <c>{algorithm}:{hex}</c> string to publish, or <see langword="null"/> for the true
    /// <c>sha256:</c> hash of <paramref name="body"/>.
    /// </param>
    /// <param name="advertisedSize">
    /// The size to publish, or <see langword="null"/> for <paramref name="body"/>'s real length.
    /// </param>
    /// <returns>The JSON body.</returns>
    public static string OneFileListJson(
        string filename,
        ReadOnlyMemory<byte> body,
        string? advertisedHash = null,
        int? advertisedSize = null)
    {
        var size = (advertisedSize ?? body.Length).ToString(CultureInfo.InvariantCulture);
        var hash = advertisedHash ?? $"sha256:{Sha256Of(body)}";

        // Concatenated rather than interpolated into a raw literal: the JSON's own closing braces
        // run up against the interpolation delimiters, and escaping them would make the wire shape
        // harder to read than the concatenation is.
        return "[{\"filename\":\"" + filename
            + "\",\"size\":" + size
            + ",\"hash\":\"" + hash
            + "\",\"urls\":{\"https\":\"" + DownloadHost + "/v0/" + DownloadSlug(filename)
            + "\",\"ftp\":\"ftp://ftp.databento.com/" + UserId + "/" + JobId + "/" + filename
            + "\"}}]";
    }

    /// <summary>The lower-case hex SHA-256 of <paramref name="body"/>.</summary>
    /// <param name="body">The bytes to hash.</param>
    /// <returns>The digest, as the API spells it after the colon.</returns>
    public static string Sha256Of(ReadOnlyMemory<byte> body) =>
        Convert.ToHexStringLower(SHA256.HashData(body.Span));

    /// <summary>
    /// A body of <paramref name="length"/> bytes whose every byte is a function of its position,
    /// so a file assembled from two transfers can be checked position by position.
    /// </summary>
    /// <remarks>
    /// Not random: a resumed download's whole question is whether byte <c>N</c> came from the first
    /// transfer or the second, and a body in which every position has a distinct expected value
    /// answers it without the test having to remember what was generated.
    /// </remarks>
    /// <param name="length">How many bytes.</param>
    /// <param name="seed">Shifts the pattern, so two bodies of the same length can differ.</param>
    /// <returns>The bytes.</returns>
    public static byte[] PatternedBody(int length, int seed = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var body = new byte[length];
        for (var i = 0; i < length; i++)
        {
            body[i] = (byte)((i * 31) + seed);
        }

        return body;
    }

    /// <summary>A throwaway directory, deleted by <see cref="TemporaryDirectory.Dispose"/>.</summary>
    /// <returns>The directory.</returns>
    public static TemporaryDirectory NewDirectory() => new();

    /// <summary>UTF-8 bytes, for the JSON fixtures served as file bodies.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}

/// <summary>A directory that deletes itself and everything under it.</summary>
/// <remarks>
/// A download writes real files, so every test here needs one. <see cref="Path.GetTempPath"/> plus
/// a GUID rather than a fixed name, because these tests run in parallel and two of them sharing an
/// output directory would each see the other's partial files as resumable transfers.
/// </remarks>
public sealed class TemporaryDirectory : IDisposable
{
    /// <summary>Creates the directory.</summary>
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"dbn-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>The directory's full path.</summary>
    public string Path { get; }

    /// <summary>The path a file of <paramref name="filename"/> lands at for the fixture job.</summary>
    /// <param name="filename">The file's name.</param>
    /// <returns>The full path.</returns>
    public string FileAt(string filename) =>
        System.IO.Path.Combine(Path, BatchFixture.JobId, filename);

    /// <summary>Deletes the directory and everything in it.</summary>
    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
