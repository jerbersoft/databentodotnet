namespace DatabentoDotNet.Dbn.Internal;

/// <summary>
/// Single seam over Zstandard decompression, which DBN uses for transport compression.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <c>ZstdSharp.Port</c>, a pure-managed port with no P/Invoke and no native asset —
/// so the package stays trim- and AOT-friendly and needs no per-RID build.
/// </para>
/// <para>
/// This type earns its place even with a single implementation behind it. .NET 11 adds
/// <c>System.IO.Compression.ZstandardStream</c> to the BCL, at which point the package can drop
/// its last third-party dependency on that framework. That target was removed in
/// <see href="https://github.com/jerbersoft/databentodotnet/issues/16">#16</see> because the
/// preview SDK is not installed on dev machines and the branch was therefore compiled nowhere.
/// Keeping every zstd call routed through here means restoring it is a one-file change.
/// </para>
/// </remarks>
internal static class ZstdDecompressor
{
    /// <summary>
    /// Wraps <paramref name="source"/> in a decompressing read-only stream.
    /// </summary>
    /// <param name="source">The compressed stream to read from.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="source"/> open when disposed.</param>
    public static Stream Decompress(Stream source, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ZstdSharp.DecompressionStream(source, leaveOpen: leaveOpen);
    }
}
