namespace DatabentoDotNet.Dbn.Enums;

/// <summary>
/// An error code from the live subscription gateway.
/// </summary>
/// <remarks>
/// Discriminants are non-contiguous: 1 through 8 are sequential, then <see cref="Unset"/> jumps
/// to 255. Upstream marks this type <c>#[non_exhaustive]</c>; Databento may add variants in a
/// future release without that being a breaking change. See <see cref="WireStrings"/> for
/// string conversions; wire strings are the mechanical <c>snake_case</c> of the variant name,
/// with no aliases.
/// </remarks>
public enum ErrorCode : byte
{
    /// <summary>The authentication step failed.</summary>
    AuthFailed = 1,

    /// <summary>The user account or API key were deactivated.</summary>
    ApiKeyDeactivated = 2,

    /// <summary>The user has exceeded their open connection limit.</summary>
    ConnectionLimitExceeded = 3,

    /// <summary>One or more symbols failed to resolve.</summary>
    SymbolResolutionFailed = 4,

    /// <summary>There was an issue with a subscription request, other than symbol resolution.</summary>
    InvalidSubscription = 5,

    /// <summary>An error occurred in the gateway.</summary>
    InternalError = 6,

    /// <summary>A slow client was detected and records were skipped by the gateway to allow catching up.</summary>
    SkippedRecordsAfterSlowReading = 7,

    /// <summary>
    /// The data for a replay subscription is no longer retained, and the schema is incompatible
    /// with skipping records.
    /// </summary>
    ReplayDataAgedOut = 8,

    /// <summary>
    /// No error code was specified, or this record was upgraded from a version 1 struct where
    /// the code field didn't exist.
    /// </summary>
    Unset = 255,
}
