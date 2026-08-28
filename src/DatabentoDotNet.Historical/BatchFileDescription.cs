namespace DatabentoDotNet.Historical;

/// <summary>One file belonging to a batch job.</summary>
/// <remarks>
/// <para>
/// Port of upstream's <c>BatchFileDesc</c> (<c>batch.rs:606-616</c>), spelt out rather than
/// abbreviated. Returned by <see cref="BatchClient.ListFilesAsync"/>, one entry per file the job
/// produced — which includes the three metadata files Databento packages with every job
/// (<c>manifest.json</c>, <c>metadata.json</c> and <c>condition.json</c>) as well as the data
/// itself.
/// </para>
/// </remarks>
public sealed record BatchFileDescription
{
    /// <summary>The file's name, without any directory part.</summary>
    /// <remarks>
    /// What <see cref="DownloadParams.Filename"/> names a single file by, and the name the file is
    /// written under inside the job's output directory. It is server-supplied, so
    /// <see cref="BatchClient.DownloadAsync"/> refuses one carrying a path separator rather than
    /// letting it choose where the file lands.
    /// </remarks>
    public required string Filename { get; init; }

    /// <summary>The file's size in bytes.</summary>
    /// <remarks>
    /// The number a resumed download compares the bytes already on disk against — see
    /// <see cref="BatchClient.DownloadAsync"/> for the three cases and what each does.
    /// </remarks>
    public required ulong Size { get; init; }

    /// <summary>The file's checksum, as <c>{algorithm}:{hex}</c> — in practice <c>sha256:…</c>.</summary>
    /// <remarks>
    /// <para>
    /// A single string rather than two fields, because that is what the wire carries. #39's probe
    /// of <c>batch.list_files</c> found every file in every job spelt
    /// <c>sha256:48eebdccd96e5670…</c>; upstream splits on the first colon and treats an algorithm
    /// it does not know as a reason to skip verification rather than to fail
    /// (<c>batch.rs:255-266</c>).
    /// </para>
    /// <para>
    /// <b>This library keeps that behaviour and makes it audible</b>, because it silently downgrades
    /// the guarantee a mismatch otherwise carries. See <see cref="BatchClient.DownloadAsync"/>.
    /// </para>
    /// </remarks>
    public required string Hash { get; init; }

    /// <summary>Where the file can be fetched from, keyed by protocol — <c>https</c> and <c>ftp</c>.</summary>
    /// <remarks>
    /// <para>
    /// A map rather than a single URL, because the API sends one:
    /// <c>{"https":"…","ftp":"ftp://…"}</c>. This library uses the <c>https</c> entry and errors
    /// when it is absent, as upstream does (<c>batch.rs:216-218</c>); nothing here speaks FTP.
    /// </para>
    /// <para>
    /// <b>Only the URL's <em>path</em> is used, and that is upstream's behaviour rather than a
    /// simplification.</b> #39 probed what the API actually returns: the <c>https</c> URL points at
    /// <c>api.databento.com</c> while the API itself is reached at <c>hist.databento.com</c> — two
    /// different hosts. Upstream's <c>get_with_path</c> (<c>client.rs:128-137</c>) joins the path
    /// onto the <em>configured</em> base URL, discarding the returned host, and both hosts were
    /// measured to serve the same bytes for the same path. See
    /// <see cref="BatchClient.DownloadAsync"/> for why keeping that matters beyond fidelity.
    /// </para>
    /// </remarks>
    public required IReadOnlyDictionary<string, string> Urls { get; init; }
}
