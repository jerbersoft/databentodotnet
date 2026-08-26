namespace DatabentoDotNet.Dbn;

/// <summary>
/// The type of <c>StatMsg</c> update.
/// </summary>
/// <remarks>
/// Purely numeric — this type has no wire string form. Upstream marks it
/// <c>#[non_exhaustive]</c>; Databento may add variants in a future release without that being
/// a breaking change. Rust marks <see cref="New"/> as the type's default, but C#'s implicit
/// <c>default(StatUpdateAction)</c> is the zero value <c>(StatUpdateAction)0</c>, which has no
/// name here — reference <see cref="New"/> explicitly where upstream's default matters.
/// </remarks>
public enum StatUpdateAction : byte
{
    /// <summary>A new statistic.</summary>
    New = 1,

    /// <summary>A removal of a statistic.</summary>
    Delete = 2,
}
