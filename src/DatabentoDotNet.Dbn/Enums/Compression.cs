namespace DatabentoDotNet.Dbn;

/// <summary>
/// A compression format, or none if uncompressed.
/// </summary>
/// <remarks>See <see cref="WireStrings"/> for string conversions. No parse-only aliases.</remarks>
public enum Compression : byte
{
    /// <summary>Uncompressed.</summary>
    None = 0,

    /// <summary>Zstandard compressed.</summary>
    Zstd = 1,
}
