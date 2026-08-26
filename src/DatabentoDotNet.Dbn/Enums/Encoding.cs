namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// A data encoding format.
/// </summary>
/// <remarks>
/// See <see cref="WireStrings"/> for string conversions. <c>dbz</c> parses as
/// <see cref="Dbn"/> — the legacy pre-rename file extension — but is never emitted.
/// </remarks>
public enum Encoding : byte
{
    /// <summary>Databento Binary Encoding.</summary>
    Dbn = 0,

    /// <summary>Comma-separated values.</summary>
    Csv = 1,

    /// <summary>JavaScript object notation.</summary>
    Json = 2,
}
