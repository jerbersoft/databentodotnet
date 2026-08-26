using System.IO.Compression;

namespace DatabentoDotNet.Dbn.Internal;

/// <summary>
/// Single seam over Zstandard decompression, which DBN uses for transport compression.
/// </summary>
/// <remarks>
/// On net11.0 this is <c>System.IO.Compression.ZstandardStream</c> from the BCL, so the package
/// carries no third-party or native dependency. On net10.0 it falls back to
/// <c>ZstdSharp.Port</c>, a pure-managed port (still no P/Invoke). Every zstd call in the
/// library goes through here so the conditional compilation stays in exactly one file.
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

#if NET11_0_OR_GREATER
        return new ZstandardStream(source, CompressionMode.Decompress, leaveOpen);
#else
        return new ZstdSharp.DecompressionStream(source, leaveOpen: leaveOpen);
#endif
    }

    /// <summary>
    /// True when Zstandard support comes from the base class library rather than a package.
    /// </summary>
    public static bool IsNativeToRuntime =>
#if NET11_0_OR_GREATER
        true;
#else
        false;
#endif
}
